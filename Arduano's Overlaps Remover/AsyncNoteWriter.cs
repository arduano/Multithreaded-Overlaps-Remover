using MIDIModificationFramework;
using MIDIModificationFramework.MIDIEvents;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Arduano_s_Overlaps_Remover
{
    class AsyncNoteWriter
    {
        ulong notesTime = 0;
        ulong evTime = 0;
        ulong time = 0;

        int halfBuffLen = 4096 * 4;

        BlockingCollection<MIDIEvent> otherEvents;
        BlockingCollection<MIDIEvent> flushBuffer;
        BlockingCollection<Note> buffer;
        AsyncNoteParse parser;
        Stream writer;
        int maxSize;
        Task writerTask = null;
        Task flusherTask = null;

        MIDIEvent currentEv = null;

        List<Note> noteOffBuffer = new List<Note>();

        public long notesWritten = 0;

        MemoryStream bufferStream = new MemoryStream();

        public bool Finalised { get; private set; } = false;

        public AsyncNoteWriter(Stream writer, BlockingCollection<MIDIEvent> otherEvents, AsyncNoteParse parser, int maxSize)
        {
            this.writer = writer;
            this.otherEvents = otherEvents;
            this.maxSize = maxSize;
            this.parser = parser;
            buffer = new BlockingCollection<Note>(maxSize * 2);
            flushBuffer = new BlockingCollection<MIDIEvent>(halfBuffLen * 2);
        }

        public void Write(Note n)
        {
            if (!buffer.TryAdd(n))
            {
                writerTask.GetAwaiter().GetResult();
                RunWriterTask();
                buffer.Add(n);
            }
            if (buffer.Count > maxSize)
            {
                if (writerTask == null) RunWriterTask();
                else if (writerTask.IsCompleted) RunWriterTask();
            }
        }

        public void RunWriterTask()
        {
            writerTask = Task.Run(() =>
            {
                lock (buffer)
                {
                    ulong laststart = 0;
                    while (buffer.Count != 0)
                    {
                        var n = buffer.Take();
                        laststart = n.Start;
                        int remove = 0;
                        foreach (var no in noteOffBuffer)
                        {
                            if (no.End > n.Start) break;
                            PushNoteEv(new NoteOffEvent((uint)(no.End - notesTime), no.Channel, no.Key));
                            remove++;
                        }
                        noteOffBuffer.RemoveRange(0, remove);
                        PushNoteEv(new NoteOnEvent((uint)(n.Start - notesTime), n.Channel, n.Key, n.Velocity));
                        int c = noteOffBuffer.Count;
                        if (noteOffBuffer.Count == 0) noteOffBuffer.Add(n);
                        else if (n.End >= noteOffBuffer.Last().End) noteOffBuffer.Add(n);
                        else
                        {
                            for (int i = 0; i < noteOffBuffer.Count; i++)
                            {
                                if (noteOffBuffer[i].End > n.End)
                                {
                                    noteOffBuffer.Insert(i, n);
                                    break;
                                }
                            }
                        }
                    }
                }
            });
        }

        void FlushEvent(MIDIEvent e)
        {
            if (!flushBuffer.TryAdd(e))
            {
                flusherTask.GetAwaiter().GetResult();
                RunFlusherTask();
                flushBuffer.Add(e);
            }
            if (flushBuffer.Count > halfBuffLen)
            {
                if (flusherTask == null || flusherTask.IsCompleted)
                    RunFlusherTask();
            }
        }

        void PushNoteEv(MIDIEvent n)
        {
            notesTime += n.DeltaTime;
            parser.ParseUpTo(notesTime);
            if (n is NoteOnEvent) notesWritten++;
            while (evTime <= notesTime)
            {
                if (currentEv == null)
                {
                    if (otherEvents.TryTake(out currentEv))
                    {
                        currentEv = currentEv.Clone();
                        evTime += currentEv.DeltaTime;
                        if (evTime <= notesTime)
                        {
                            currentEv.DeltaTime = (uint)(evTime - time);
                            FlushEvent(currentEv);
                            currentEv = null;
                            time = evTime;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    currentEv.DeltaTime = (uint)(evTime - time);
                    FlushEvent(currentEv);
                    currentEv = null;
                    time = evTime;
                }
            }
            n.DeltaTime = (uint)(notesTime - time);
            FlushEvent(n);
            time = notesTime;
        }

        public void Finalise()
        {
            if (Finalised) return;
            buffer.CompleteAdding();
            Join();
            RunWriterTask();
            Join();
            foreach (var n in noteOffBuffer)
            {
                PushNoteEv(new NoteOffEvent((uint)(n.End - notesTime), n.Channel, n.Key));
            }
            while (otherEvents.Count != 0)
            {
                FlushEvent(otherEvents.Take());
            }
            if (flusherTask != null)
                flusherTask.GetAwaiter().GetResult();
            RunFlusherTask();
            flusherTask.GetAwaiter().GetResult();
            bufferStream.Dispose();
            Finalised = true;
        }

        void RunFlusherTask()
        {
            flusherTask = Task.Run(() =>
            {
                while (flushBuffer.Count != 0)
                {
                    var b = flushBuffer.Take().GetDataWithDelta();
                    bufferStream.Write(b, 0, b.Length);
                }
                try
                {
                    byte[] a = new byte[bufferStream.Position];
                    bufferStream.Position = 0;
                    bufferStream.Read(a, 0, a.Length);
                    writer.Write(a, 0, a.Length);
                    bufferStream.SetLength(0);
                }
                catch (ObjectDisposedException) { }
            });
        }

        public void Join()
        {
            if (writerTask != null) writerTask.GetAwaiter().GetResult();
        }

        public void Close()
        {
            writer.Close();
        }
    }
}
