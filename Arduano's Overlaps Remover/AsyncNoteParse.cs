using MIDIModificationFramework;
using MIDIModificationFramework.MIDIEvents;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Arduano_s_Overlaps_Remover
{
    class AsyncNoteParse
    {
        class UnendedNote : Note
        {
            public UnendedNote(byte channel, byte key, byte vel, ulong start, ulong end) : base(channel, key, vel, start, end) { }
            public bool ended = false;
        }

        BlockingCollection<Note> content = new BlockingCollection<Note>();
        ulong time = 0;
        IEnumerator<MIDIEvent> src;
        BlockingCollection<MIDIEvent> otherEvents;
        Task parserTask = null;
        int targetCapactity;
        FastList<UnendedNote>[] unendedNotes = new FastList<UnendedNote>[128 * 16];
        FastList<UnendedNote> notesQueue = new FastList<UnendedNote>();

        public ulong Time => time;

        bool srcEnded = false;

        uint extraTime;

        public bool ParseNextEv()
        {
            if (!src.MoveNext())
            {
                srcEnded = true;
                content.CompleteAdding();
                unendedNotes = null;
                return false;
            }
            var e = src.Current;
            time += e.DeltaTime;
            if (e is NoteOnEvent)
            {
                extraTime += e.DeltaTime;
                var n = e as NoteOnEvent;
                var note = new UnendedNote(n.Channel, n.Key, n.Velocity, time, 0);
                if(unendedNotes == null) unendedNotes = new FastList<UnendedNote>[128 * 16];
                unendedNotes[n.Key * 16 + n.Channel].Add(note);
                notesQueue.Add(note);
            }
            else if (e is NoteOffEvent)
            {
                extraTime += e.DeltaTime;
                var n = e as NoteOffEvent;
                if (unendedNotes != null)
                {
                    var a = unendedNotes[n.Key * 16 + n.Channel];
                    if (!a.ZeroLen)
                    {
                        var note = a.Pop();
                        note.ended = true;
                        note.End = time;
                    }
                }
            }
            else
            {
                e.DeltaTime += extraTime;
                extraTime = 0;
                otherEvents.Add(e);
            }
            return true;
        }

        public bool IsCompleted => content.IsCompleted;

        public bool GetNextNote()
        {
            if (srcEnded) return false;
            while (notesQueue.First == null || notesQueue.First.ended == false)
            {
                if (!ParseNextEv()) return false;
            }
            content.Add(notesQueue.Pop());
            return true;
        }

        public AsyncNoteParse(IEnumerable<MIDIEvent> src, BlockingCollection<MIDIEvent> otherEvents, int targetCapactity)
        {
            this.targetCapactity = targetCapactity;
            this.otherEvents = otherEvents;
            this.src = src.GetEnumerator();
            for (int i = 0; i < unendedNotes.Length; i++) unendedNotes[i] = new FastList<UnendedNote>();
        }

        public void Init()
        {
            RunParserTask();
        }

        public Note Take()
        {
            Note n;
            if(!content.TryTake(out n))
            {
                if (content.IsCompleted) throw new Exception();
                parserTask.GetAwaiter().GetResult();
                RunParserTask();
                n = content.Take();
            }
            if(content.Count < targetCapactity / 2 && !srcEnded)
            {
                if (parserTask == null) RunParserTask();
                else if (parserTask.IsCompleted) RunParserTask();
            }
            return n;
        }

        void RunParserTask()
        {
            parserTask = Task.Run(() =>
            {
                lock (content)
                {
                    while (content.Count < targetCapactity && GetNextNote())
                        continue;
                }
            });
        }

        public void ParseUpTo(ulong targetTime)
        {
            if (targetTime < time) return;
            Task.Run(() =>
            {
                lock (content)
                {
                    while (time <= targetTime && ParseNextEv())
                        continue;
                }
            }).GetAwaiter().GetResult();
        }
    }
}
