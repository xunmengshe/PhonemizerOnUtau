using OpenUtau.Core.Ustx;
using utauPlugin;

namespace PhonemizerOnUtau {
    public class UtauUtil {
        public static bool isRest(Note note) {
            var lyric = note.GetLyric();
            return lyric == "R" || lyric == "r";
        }

        //get a list of positions for each note
        public static List<MyNote> PluginNotesToUNotes(UtauPlugin plugin) {
            var myNotes = new List<MyNote>();
            var currentPosition = 0;
            foreach (var note in plugin.note) {
                
                var duration = note.GetLength();
                if (!(isRest(note)) && note.GetNum()!="PREV" && note.GetNum() != "NEXT") {
                    var myNote = new MyNote {
                        position = currentPosition,
                        duration = duration,
                        tone = note.GetNoteNum(),
                        lyric = note.GetLyric(),
                        utauNote = note
                    };
                    myNotes.Add(myNote);
                }
                currentPosition += duration;
            }
            return myNotes;
        }

        public static List<UTempo> GetTempoList(UtauPlugin plugin, List<MyNote> myNotes) {
            var tempos = new List<UTempo>{
                new UTempo(0, plugin.Tempo)
            };
            tempos.AddRange(myNotes
                .Select(n => n.GetTempo())
                .Where(t => t != null));
            //Remove duplicate tempos
            tempos = tempos.Zip(tempos.Skip(1))
                .Where(tu => tu.First.bpm != tu.Second.bpm)
                .Select(tu => tu.Second)
                .Prepend(tempos[0])
                .ToList();

            return tempos;
        }
    }
}
