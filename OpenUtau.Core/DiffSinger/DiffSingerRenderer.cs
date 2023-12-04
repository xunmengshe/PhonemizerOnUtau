﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NumSharp;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.DiffSinger {
    public class DiffSingerRenderer : IRenderer {
        const float headMs = DiffSingerUtils.headMs;
        const float tailMs = DiffSingerUtils.tailMs;
        const string VELC = DiffSingerUtils.VELC;
        const string ENE = DiffSingerUtils.ENE;
        const string PEXP = DiffSingerUtils.PEXP;
        const string VoiceColorHeader = DiffSingerUtils.VoiceColorHeader;

        static readonly HashSet<string> supportedExp = new HashSet<string>(){
            Format.Ustx.DYN,
            Format.Ustx.PITD,
            Format.Ustx.GENC,
            Format.Ustx.CLR,
            Format.Ustx.BREC,
            VELC,
            ENE,
            PEXP,
        };

        static readonly object lockObj = new object();

        public USingerType SingerType => USingerType.DiffSinger;

        public bool SupportsRenderPitch => true;

        public bool IsVoiceColorCurve(string abbr, out int subBankId) {
            subBankId = 0;
            if (abbr.StartsWith(VoiceColorHeader) && int.TryParse(abbr.Substring(2), out subBankId)) {;
                subBankId -= 1;
                return true;
            } else {
                return false;
            }
        }

        public bool SupportsExpression(UExpressionDescriptor descriptor) {
            return supportedExp.Contains(descriptor.abbr) || 
                (descriptor.abbr.StartsWith(VoiceColorHeader) && int.TryParse(descriptor.abbr.Substring(2), out int _));
        }

        //Calculate the Timing layout of the RenderPhrase, 
        //including the position of the phrase, 
        //the length of the head consonant, and the estimated total length
        public RenderResult Layout(RenderPhrase phrase) {
            return new RenderResult() {
                leadingMs = headMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = headMs + phrase.durationMs + tailMs,
            };
        }

        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo, CancellationTokenSource cancellation, bool isPreRender) {
            var task = Task.Run(() => {
                lock (lockObj) {
                    if (cancellation.IsCancellationRequested) {
                        return new RenderResult();
                    }
                    var result = Layout(phrase);

                    // calculate real depth
                    int speedup = Core.Util.Preferences.Default.DiffsingerSpeedup;
                    var singer = (DiffSingerSinger) phrase.singer;
                    int depth = Core.Util.Preferences.Default.DiffSingerDepth;
                    if (singer.dsConfig.useShallowDiffusion) {
                        int kStep = singer.dsConfig.maxDepth;
                        if (kStep < 0) {
                            throw new InvalidDataException("Max depth is unset or is negative.");
                        }
                        depth = Math.Min(depth, kStep);  // make sure depth <= K_step
                        depth = depth / speedup * speedup;  // make sure depth can be divided by speedup
                    }
                    var wavName = singer.dsConfig.useShallowDiffusion
                        ? $"ds-{phrase.hash:x16}-depth{depth}-{speedup}x.wav"  // if the depth changes, phrase should be re-rendered
                        : $"ds-{phrase.hash:x16}-{speedup}x.wav";  // preserve this for not invalidating cache from older versions
                    var wavPath = Path.Join(PathManager.Inst.CachePath, wavName);
                    string progressInfo = $"Track {trackNo + 1}: {this}{speedup}x \"{string.Join(" ", phrase.phones.Select(p => p.phoneme))}\"";
                    if (File.Exists(wavPath)) {
                        try {
                            using (var waveStream = Wave.OpenFile(wavPath)) {
                                result.samples = Wave.GetSamples(waveStream.ToSampleProvider().ToMono(1, 0));
                            }
                        } catch (Exception e) {
                            Log.Error(e, "Failed to render.");
                        }
                    }
                    if (result.samples == null) {
                        result.samples = InvokeDiffsinger(phrase, depth, speedup);
                        var source = new WaveSource(0, 0, 0, 1);
                        source.SetSamples(result.samples);
                        WaveFileWriter.CreateWaveFile16(wavPath, new ExportAdapter(source).ToMono(1, 0));
                    }
                    if (result.samples != null) {
                        Renderers.ApplyDynamics(phrase, result);
                    }
                    progress.Complete(phrase.phones.Length, progressInfo);
                    return result;
                }
            });
            return task;
        }
        /*result format: 
        result.samples: Rendered audio, float[]
        leadingMs、positionMs、estimatedLengthMs: timeaxis layout in Ms, double
         */

        float[] InvokeDiffsinger(RenderPhrase phrase, int depth, int speedup) {
            var singer = phrase.singer as DiffSingerSinger;
            //Check if dsconfig.yaml is correct
            if(String.IsNullOrEmpty(singer.dsConfig.vocoder) ||
                String.IsNullOrEmpty(singer.dsConfig.acoustic) ||
                String.IsNullOrEmpty(singer.dsConfig.phonemes)){
                throw new Exception("Invalid dsconfig.yaml. Please ensure that dsconfig.yaml contains keys \"vocoder\", \"acoustic\" and \"phonemes\".");
            }

            var vocoder = singer.getVocoder();
            var frameMs = vocoder.frameMs();
            var frameSec = frameMs / 1000;
            int headFrames = (int)Math.Round(headMs / frameMs);
            int tailFrames = (int)Math.Round(tailMs / frameMs);
            var result = Layout(phrase);
            //acoustic
            //mel = session.run(['mel'], {'tokens': tokens, 'durations': durations, 'f0': f0, 'speedup': speedup})[0]
            //tokens: phoneme index in the phoneme set
            //durations: phoneme duration in frames
            //f0: pitch curve in Hz by frame
            //speedup: Diffusion render speedup, int
            var tokens = phrase.phones
                .Select(p => p.phoneme)
                .Prepend("SP")
                .Append("SP")
                .Select(x => (long)(singer.phonemes.IndexOf(x)))
                .ToList();
            var durations = phrase.phones
                .Select(p => (int)Math.Round(p.endMs / frameMs) - (int)Math.Round(p.positionMs / frameMs))//prevent cumulative error
                .Prepend(headFrames)
                .Append(tailFrames)
                .ToList();
            int totalFrames = durations.Sum();
            float[] f0 = DiffSingerUtils.SampleCurve(phrase, phrase.pitches, 0, frameMs, totalFrames, headFrames, tailFrames, 
                x => MusicMath.ToneToFreq(x * 0.01))
                .Select(f => (float)f).ToArray();
            //toneShift isn't supported

            var acousticInputs = new List<NamedOnnxValue>();
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("tokens",
                new DenseTensor<long>(tokens.ToArray(), new int[] { tokens.Count },false)
                .Reshape(new int[] { 1, tokens.Count })));
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("durations",
                new DenseTensor<long>(durations.Select(x=>(long)x).ToArray(), new int[] { durations.Count }, false)
                .Reshape(new int[] { 1, durations.Count })));
            var f0tensor = new DenseTensor<float>(f0, new int[] { f0.Length })
                .Reshape(new int[] { 1, f0.Length });
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("f0",f0tensor));

            // sampling acceleration related
            if (singer.dsConfig.useShallowDiffusion) {
                acousticInputs.Add(NamedOnnxValue.CreateFromTensor("depth",
                    new DenseTensor<long>(new long[] { depth }, new int[] { 1 }, false)));
            }
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("speedup",
                new DenseTensor<long>(new long[] { speedup }, new int[] { 1 },false)));

            //speaker
            if(singer.dsConfig.speakers != null) {
                var speakerEmbedManager = singer.getSpeakerEmbedManager();
                var spkEmbedTensor = speakerEmbedManager.PhraseSpeakerEmbedByFrame(phrase, durations, frameMs, totalFrames, headFrames, tailFrames);
                acousticInputs.Add(NamedOnnxValue.CreateFromTensor("spk_embed", spkEmbedTensor));
            }
            //gender
            //Definition of GENC: 100 = 12 semitones of formant shift, positive GENC means shift down
            if (singer.dsConfig.useKeyShiftEmbed) {
                var range = singer.dsConfig.augmentationArgs.randomPitchShifting.range;
                var positiveScale = (range[1]==0) ? 0 : (12/range[1]/100);
                var negativeScale = (range[0]==0) ? 0 : (-12/range[0]/100);
                float[] gender = DiffSingerUtils.SampleCurve(phrase, phrase.gender, 
                    0, frameMs, totalFrames, headFrames, tailFrames,
                    x=> (x<0)?(-x * positiveScale):(-x * negativeScale))
                    .Select(f => (float)f).ToArray();
                var genderTensor = new DenseTensor<float>(gender, new int[] { gender.Length })
                    .Reshape(new int[] { 1, gender.Length });
                acousticInputs.Add(NamedOnnxValue.CreateFromTensor("gender", genderTensor));
            }

            //velocity
            //Definition of VELC: logarithmic scale, Default value 100 = original speed, 
            //each 100 increase means speed x2
            if (singer.dsConfig.useSpeedEmbed) {
                var velocityCurve = phrase.curves.FirstOrDefault(curve => curve.Item1 == VELC);
                float[] velocity;
                if (velocityCurve != null) {
                    velocity = DiffSingerUtils.SampleCurve(phrase, velocityCurve.Item2,
                        1, frameMs, totalFrames, headFrames, tailFrames,
                        x => Math.Pow(2, (x - 100) / 100))
                        .Select(f => (float)f).ToArray();
                } else {
                    velocity = Enumerable.Repeat(1f, totalFrames).ToArray();
                }
                var velocityTensor = new DenseTensor<float>(velocity, new int[] { velocity.Length })
                    .Reshape(new int[] { 1, velocity.Length });
                acousticInputs.Add(NamedOnnxValue.CreateFromTensor("velocity", velocityTensor));
            }

            //Variance: Energy and Breathiness
            if(singer.dsConfig.useBreathinessEmbed || singer.dsConfig.useEnergyEmbed){
                var varianceResult = singer.getVariancePredictor().Process(phrase);
                //TODO: let user edit variance curves
                if(singer.dsConfig.useEnergyEmbed){
                    var energyCurve = phrase.curves.FirstOrDefault(curve => curve.Item1 == ENE);
                    IEnumerable<double> userEnergy;
                    if(energyCurve!=null){
                        userEnergy = DiffSingerUtils.SampleCurve(phrase, energyCurve.Item2,
                            0, frameMs, totalFrames, headFrames, tailFrames,
                            x => x);
                    } else{
                        userEnergy = Enumerable.Repeat(0d, totalFrames);
                    }
                    var energy = varianceResult.energy.Zip(userEnergy, (x,y)=>(float)Math.Min(x + y*12/100, 0)).ToArray();
                    acousticInputs.Add(NamedOnnxValue.CreateFromTensor("energy", 
                        new DenseTensor<float>(energy, new int[] { energy.Length })
                        .Reshape(new int[] { 1, energy.Length })));
                }
                if(singer.dsConfig.useBreathinessEmbed){
                    var userBreathiness = DiffSingerUtils.SampleCurve(phrase, phrase.breathiness,
                        0, frameMs, totalFrames, headFrames, tailFrames,
                        x => x);
                    var breathiness = varianceResult.breathiness.Zip(userBreathiness, (x,y)=>(float)Math.Min(x + y*12/100, 0)).ToArray();
                    acousticInputs.Add(NamedOnnxValue.CreateFromTensor("breathiness", 
                        new DenseTensor<float>(breathiness, new int[] { breathiness.Length })
                        .Reshape(new int[] { 1, breathiness.Length })));
                }
            }

            var acousticModel = singer.getAcousticSession();
            Onnx.VerifyInputNames(acousticModel, acousticInputs);
            Tensor<float> mel;
            var acousticOutputs = acousticModel.Run(acousticInputs);
            mel = acousticOutputs.First().AsTensor<float>().Clone();
            
            //vocoder
            //waveform = session.run(['waveform'], {'mel': mel, 'f0': f0})[0]
            var vocoderInputs = new List<NamedOnnxValue>();
            vocoderInputs.Add(NamedOnnxValue.CreateFromTensor("mel", mel));
            vocoderInputs.Add(NamedOnnxValue.CreateFromTensor("f0",f0tensor));
            float[] samples;
            var vocoderOutputs = vocoder.session.Run(vocoderInputs);
            samples = vocoderOutputs.First().AsTensor<float>().ToArray();
            return samples;
        }

        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            return (phrase.singer as DiffSingerSinger).getPitchPredictor().Process(phrase);
        }

        public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) {
            var result = new List<UExpressionDescriptor> {
                //velocity
                new UExpressionDescriptor{
                    name="velocity (curve)",
                    abbr=VELC,
                    type=UExpressionType.Curve,
                    min=0,
                    max=200,
                    defaultValue=100,
                    isFlag=false,
                },
                //energy
                new UExpressionDescriptor{
                    name="energy (curve)",
                    abbr=ENE,
                    type=UExpressionType.Curve,
                    min=-100,
                    max=100,
                    defaultValue=0,
                    isFlag=false,
                },
                //expressiveness
                new UExpressionDescriptor {
                    name = "pitch expressiveness (curve)",
                    abbr = PEXP,
                    type = UExpressionType.Curve,
                    min = 0,
                    max = 100,
                    defaultValue = 100,
                    isFlag = false
                },
            };
            //speakers
            var dsSinger = singer as DiffSingerSinger;
            if(dsSinger!=null && dsSinger.dsConfig.speakers != null) {
                result.AddRange(Enumerable.Zip(
                    dsSinger.Subbanks,
                    Enumerable.Range(1, dsSinger.Subbanks.Count),
                    (subbank,index)=>new UExpressionDescriptor {
                        name=$"voice color {subbank.Color}",
                        abbr=VoiceColorHeader+index.ToString("D2"),
                        type=UExpressionType.Curve,
                        min=0,
                        max=100,
                        defaultValue=0,
                        isFlag=false,
                    }));
            }
            //energy

            return result.ToArray();
        }

        public override string ToString() => Renderers.DIFFSINGER;
    }
}
