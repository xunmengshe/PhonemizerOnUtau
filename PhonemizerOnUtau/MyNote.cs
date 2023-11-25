using OpenUtau.Core.Ustx;
using OpenUtau.Api;

using utauPlugin;

namespace PhonemizerOnUtau {
    public class MyNote {
        /// <summary>
        /// Note class defined in this plugin, containing the link to phonemizer note and utau note
        /// </summary>
        public int position;
        public int duration;
        public int tone;
        public string lyric = "";
        public Note? utauNote;

        public int end => position + duration;

        //if the plugin note has tempo change, return its UTempo
        //otherwise return null
        public UTempo? GetTempo() {
            if (utauNote != null && utauNote.HasTempo()) {
                return new UTempo(position, utauNote.GetTempo());
            }
            return null;
        }

        public Phonemizer.Note ToPhonemizerNote() {
            return new Phonemizer.Note() {
                position = position,
                duration = duration,
                lyric = lyric,
                tone = tone
            };
        }

        public bool IsSlur() {
            return lyric.StartsWith("+");
        }
    }
}
