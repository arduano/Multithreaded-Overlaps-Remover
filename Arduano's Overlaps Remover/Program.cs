using Microsoft.Win32;
using MIDIModificationFramework;
using MIDIModificationFramework.MIDIEvents;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Arduano_s_Overlaps_Remover
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string filepath;
            if (args.Length > 0)
            {
                filepath = args[0];
            }
            else
            {
                Console.WriteLine("Select MIDI");
                var open = new OpenFileDialog();
                open.Filter = "Midi files (*.mid)|*.mid";
                if ((bool)open.ShowDialog())
                {
                    filepath = open.FileName;
                }
                else return;
            }

            string output;
            if (args.Length == 1)
            {
                var ext = Path.GetExtension(filepath);
                output = filepath.Substring(filepath.Length - ext.Length, ext.Length) + "_OR.mid";
            }
            else if (args.Length > 2)
            {
                output = args[1];
            }
            else
            {
                Console.WriteLine("Save file...");
                var save = new SaveFileDialog();
                save.Filter = "Midi files (*.mid)|*.mid";
                if ((bool)save.ShowDialog())
                {
                    output = save.FileName;
                }
                else return;
            }

            MidiFile file;
            ParallelStream tempOutput;
            Stream trueOutput;
            try
            {
                file = new MidiFile(() => new BufferedStream(File.Open(filepath, FileMode.Open, FileAccess.Read, FileShare.Read), 4096 * 4));
            }
            catch (IOException)
            {
                Console.WriteLine("Could not open input file\nPress any key to exit...");
                Console.ReadKey();
                return;
            }
            try
            {
                var s = File.Open(output + ".tmp", FileMode.Create);
                tempOutput = new ParallelStream(s, 4096 * 16);
            }
            catch (IOException)
            {
                Console.WriteLine("Could not open temporary output file\nPress any key to exit...");
                Console.ReadKey();
                return;
            }
            try
            {
                trueOutput = File.Open(output, FileMode.Create);
            }
            catch (IOException)
            {
                Console.WriteLine("Could not open true output file\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("Processing the midi...");

            BlockingCollection<MIDIEvent>[] cacheEvents = new BlockingCollection<MIDIEvent>[file.TrackCount];
            AsyncNoteParse[] inputNotes = new AsyncNoteParse[file.TrackCount];
            AsyncNoteWriter[] outputNotes = new AsyncNoteWriter[file.TrackCount];
            Note[] nextNotes = new Note[file.TrackCount];
            Note[] topNotes = new Note[128];
            Queue<Note>[] fullNotes = new Queue<Note>[file.TrackCount];

            for (int i = 0; i < file.TrackCount; i++)
            {
                Console.WriteLine("Loading Tracks " + (i + 1) + "/" + file.TrackCount);
                cacheEvents[i] = new BlockingCollection<MIDIEvent>();

                var reader = file.GetAsyncBufferedTrack(i, 10000);
                inputNotes[i] = new AsyncNoteParse(reader, cacheEvents[i], 128);
                inputNotes[i].Init();
                outputNotes[i] = new AsyncNoteWriter(tempOutput.GetStream(i), cacheEvents[i], inputNotes[i], 128);
                fullNotes[i] = new Queue<Note>();
            }

            for (int i = 0; i < file.TrackCount; i++)
            {
                try
                {
                    nextNotes[i] = inputNotes[i].Take();
                }
                catch { }
            }

            bool allEnded = false;
            bool addedNotes = false;
            long nc = 0;
            long nc2 = 0;
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Task writeTask = null;
            for (ulong tick = 0; !allEnded; )
            {
                for (int i = 0; i < topNotes.Length; i++)
                    topNotes[i] = null;
                allEnded = true;
                addedNotes = false;
                ulong nextSmallest = 0;
                bool first = true;
                for (int i = 0; i < file.TrackCount; i++)
                {
                    while (nextNotes[i] != null && nextNotes[i].Start == tick)
                    {
                        addedNotes = true;
                        var n = nextNotes[i];

                        if (writeTask != null)
                            if (!writeTask.IsCompleted)
                            {
                                //Stopwatch d = new Stopwatch();
                                //d.Start();
                                writeTask.GetAwaiter().GetResult();
                                //Console.WriteLine(d.ElapsedTicks);
                            }

                        if (topNotes[n.Key] == null)
                        {
                            topNotes[n.Key] = n;
                            fullNotes[i].Enqueue(n);
                        }
                        else
                        {
                            if (topNotes[n.Key].End < n.End)
                            {
                                topNotes[n.Key] = n;
                                fullNotes[i].Enqueue(n);
                            }
                            else
                            {
                                if (topNotes[n.Key].Velocity < n.Velocity)
                                    topNotes[n.Key].Velocity = n.Velocity;
                            }
                        }

                        try
                        {
                            nextNotes[i] = inputNotes[i].Take();
                            nc++;
                        }
                        catch
                        {
                            nextNotes[i] = null;
                        }
                    }

                    if (nextNotes[i] != null)
                    {
                        allEnded = false;
                        if (first || nextNotes[i].Start < nextSmallest)
                        {
                            first = false;
                            nextSmallest = nextNotes[i].Start;
                        }
                    }
                }
                if (!allEnded)
                    tick = nextSmallest;
                if (addedNotes)
                {
                    writeTask = Task.Run(() =>
                    {
                        Parallel.For(0, file.TrackCount, i =>
                        {
                            var fn = fullNotes[i];
                            while (fn.Count != 0)
                            {
                                outputNotes[i].Write(fn.Dequeue());
                                //fn.Dequeue();
                                nc2++;
                            }
                            if (nextNotes[i] == null && !outputNotes[i].Finalised)
                                try
                                {
                                    outputNotes[i].Finalise();
                                }
                                catch { }
                        });
                    });
                }

                if (sw.ElapsedMilliseconds > 1000)
                {
                    Console.WriteLine("Processed: " + nc.ToString("#,##0") + "\tKept: " + nc2.ToString("#,##0"));
                    sw.Reset();
                    sw.Start();
                }
            }
            Console.WriteLine("Processed: " + nc.ToString("#,##0") + "\tKept: " + nc2.ToString("#,##0"));

            Console.WriteLine("Waiting for all writers to finish...");
            writeTask.GetAwaiter().GetResult();
            for (int i = 0; i < file.TrackCount; i++)
                if (!outputNotes[i].Finalised)
                {
                    try
                    {
                        outputNotes[i].Finalise();
                    }
                    catch { }
                }
            for (int i = 0; i < file.TrackCount; i++)
                outputNotes[i].Join();
            for (int i = 0; i < file.TrackCount; i++)
                outputNotes[i].Close();

            Console.WriteLine("Writing final midi");
            var writer = new MidiWriter(trueOutput);
            writer.Init();
            writer.WriteNtrks((ushort)file.TrackCount);
            writer.WritePPQ(file.PPQ);
            writer.WriteFormat(file.Format);
            for (int i = 0; i < file.TrackCount; i++)
            {
                Console.WriteLine("Copying Track " + i + "/" + file.TrackCount);
                writer.InitTrack();
                var r = tempOutput.GetStream(i, true);
                r.CopyTo(trueOutput);
                r.Close();
                writer.EndTrack();
            }
            writer.Close();
            tempOutput.CloseAllStreams();
            tempOutput.Dispose();
            try
            {
                File.Delete(output + ".tmp");
            }
            catch
            {
                Console.WriteLine("Couldn't delete temporary file");
            }
            Console.WriteLine("Complete!\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}
