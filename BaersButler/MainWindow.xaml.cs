using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Newtonsoft.Json;

namespace BaersButler
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // **************************************** //
        // *************** On Load **************** //
        // **************************************** //
        public MainWindow()
        {
            InitializeComponent();
        }


        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            init();
            setState(STATE.START);
        }


        // **************************************** //
        // *************** SETTINGS *************** //
        // **************************************** //
        public static readonly string DefaultLogDirectory = System.IO.Path.Combine(
           Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");
        public static string LogDirectory = DefaultLogDirectory;
        public static int MonitorFrequency = 500; //in ms
        public static Uri ReportServer; //server to report too TODO make it
        public static List<string> RoomsToMonitor = new List<string>() { "Dropbaers Anonymous", "Brave Propaganda" };


        long reported = 0;
        long failed = 0;

        private DateTime LastMsgReported = DateTime.MinValue;

        private STATE state = STATE.INIT;
        private System.Timers.Timer timerEveProcessCheck = new System.Timers.Timer();
        private System.Timers.Timer timerFileDiscover = new System.Timers.Timer();
        private System.Timers.Timer timerFileReader = new System.Timers.Timer();
        private System.Timers.Timer timerConfigCheck = new System.Timers.Timer();
        private bool eveRunningLast = false;
        private static Object readerLock = new Object(); // Ensures that only one thread can read files at a time.

        private Dictionary<String, FileInfo> roomToFile = new Dictionary<String, FileInfo>();
        private Dictionary<String, String> roomToLastLine = new Dictionary<String, String>();
        private Dictionary<FileInfo, long> fileToOffset = new Dictionary<FileInfo, long>();

        enum STATE
        {
            INIT,
            START,
            RUNNING,
            DOWNTIME,
            STOP
        };

        // **************************************** //
        // *************** Program **************** //
        // **************************************** //

        private Boolean isEveRunning()
        {
            return (Process.GetProcesses().Where(p => p.ProcessName.ToLower() == "exefile").ToList().Count() != 0);
        }

        private void appendText(string msgLine)
        {
            Dispatcher.Invoke((Action)(() => MsgReportText.AppendText(msgLine + "\r\n")));
        }

        private void setState(STATE nState)
        {
            if (state == nState)
            {
                return;
            }

            state = nState;

            if (STATE.START == nState)
            {
                execEveTimer(null, null);
                execFileDiscoverTimer(null, null);
                execFileReaderTimer(null, null);

                timerFileDiscover.Start();
                timerFileReader.Interval = MonitorFrequency;
                timerFileReader.Start();
                //ReportMsg(string.Empty, "start");
                appendText("EVE State Change.  Current State: " + Enum.GetName(typeof(STATE), state));
                setState(STATE.RUNNING);
            }
            if (STATE.STOP == nState)
            {
                timerFileDiscover.Stop();
                timerFileReader.Stop();
                //ReportMsg(string.Empty, "stop");
                appendText("EVE State Change.  Current State: " + Enum.GetName(typeof(STATE), state));
            }
            if (STATE.DOWNTIME == nState)
            {
                timerFileReader.Stop();
                //ReportMsg(string.Empty, "stop");
                appendText("EVE State Change.  Current State: " + Enum.GetName(typeof(STATE), state));
            }
        }

        private void updateLatestMonitoredFiles()
        {
            if (LastMsgReported > (DateTime.Now.AddMilliseconds(-1 * timerFileDiscover.Interval))) return; //If chat has been reported recently, we don't need to recheck.
            if (DateTime.UtcNow.TimeOfDay > new TimeSpan(10, 59, 00) && DateTime.UtcNow.TimeOfDay < new TimeSpan(11, 05, 00))
            {
                setState(STATE.DOWNTIME);
                appendText("Downtime Detected.  Waiting for new chat logs to be created.");
            }
            if (state == STATE.RUNNING)
            {
                // Option for webserver to recieve heartbeats
                //appendText("Sending heartbeat.");
                //ReportMsg(string.Empty, "Running");
            }
            appendText("Updating chatlog file list.");
            string oldfiles = string.Empty;
            foreach (FileInfo fi in roomToFile.Values)
                oldfiles += fi.Name + ", ";

            string report = string.Empty;
            foreach (String roomName in RoomsToMonitor)
            {
                //Debug.WriteLine("Checking for : " + roomName);

                FileInfo[] files = new DirectoryInfo(LogDirectory)
                        .GetFiles(roomName + "_*.txt", SearchOption.TopDirectoryOnly);
                FileInfo fi = files.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();

                if (fi == null)
                {
                    continue;
                }

                //Debug.WriteLine("KIU Latest: " + fi);

                // Check if eve has opened this file -> Eve is running and user has joined channel
                Boolean inUse = false;
                try
                {
                    FileStream fs = fi.Open(FileMode.Open, FileAccess.Read, FileShare.None);
                    fs.Close();
                }
                catch
                {
                    inUse = true;
                }

                if (!inUse)
                {
                    //Debug.WriteLine("KIU Skipping: " + fi);
                    continue;
                }

                //Debug.WriteLine("KIU Using: " + fi);
                roomToFile[roomName] = fi;
                report += fi.Name + "\r\n";
            }

            // Clear offset list of old files if necessary.
            List<FileInfo> deletethese = new List<FileInfo>();
            foreach (FileInfo fi in fileToOffset.Keys)
                if (!roomToFile.ContainsValue(fi)) deletethese.Add(fi);
            foreach (FileInfo fi in deletethese)
                fileToOffset.Remove(fi);

            // If a new file is created, recheck to make sure we have the most up to date log files.
            FileSystemWatcher watcher = new FileSystemWatcher(LogDirectory);
            watcher.NotifyFilter = NotifyFilters.CreationTime;
            watcher.Created += new FileSystemEventHandler(FileCreated);
            watcher.EnableRaisingEvents = true;

            string newfiles = string.Empty;
            foreach (FileInfo fi in roomToFile.Values)
                newfiles += fi.Name + ", ";
            if (!newfiles.Equals(oldfiles))
            {
                if (newfiles.Length > 2) newfiles = newfiles.Substring(0, newfiles.Length - 2); // trim the last comma and space
                if (oldfiles.Length > 2) oldfiles = oldfiles.Substring(0, oldfiles.Length - 2); // trim the last comma and space
                if (state == STATE.RUNNING || state == STATE.DOWNTIME) appendText(string.Format("Chat Files Changed. Old Files: {0}, New Files: {1}", oldfiles, newfiles));
                if (state == STATE.DOWNTIME) setState(STATE.START);
            }
            Dispatcher.Invoke((Action)(() => monitorFilesLabel.Content = report));
        }

        private void FileCreated(object sender, FileSystemEventArgs e)
        {
            appendText("New File Detected: " + e.Name);
            updateLatestMonitoredFiles();
        }

        private void execEveTimer(object sender, EventArgs e)
        {
            Boolean eveRunning = isEveRunning();
            if (eveRunning == eveRunningLast)
            {
                return;
            }
            eveRunningLast = eveRunning;
            if (eveRunning)
            {
                setState(STATE.START);
            }
            else
            {
                setState(STATE.STOP);
            }
        }

        private void execFileDiscoverTimer(object sender, EventArgs e)
        {
            updateLatestMonitoredFiles();
        }

        private void init()
        {
            timerEveProcessCheck.Elapsed += new ElapsedEventHandler(execEveTimer);
            timerEveProcessCheck.Interval = 1000 * 60 * 1;
            timerEveProcessCheck.Start();

            timerFileDiscover.Elapsed += new ElapsedEventHandler(execFileDiscoverTimer);
            timerFileDiscover.Interval = 1000 * 60 * 2;

            timerFileReader.Elapsed += new ElapsedEventHandler(execFileReaderTimer);
        }

        private void execFileReaderTimer(object sender, EventArgs e)
        {
            if (!Monitor.TryEnter(readerLock))
            {
                Debug.WriteLine("File Reader Thread: Locked");
                return; // Ensures that only one thread can read files at a time.
            }
            FileStream logFileStream;
            StreamReader logFileReader;

            String line;

            try
            {
                foreach (String roomName in RoomsToMonitor)
                {
                    FileInfo logfile = null;
                    roomToFile.TryGetValue(roomName, out logfile);

                    if (logfile == null)
                    {
                        //Debug.WriteLine("KIU Skipping room: " + roomName);
                        continue;
                    }

                    long offset = 0;
                    fileToOffset.TryGetValue(logfile, out offset);

                    logfile.Refresh();
                    //Debug.WriteLine("Offset: " + offset.ToString());
                    //Debug.WriteLine("File Length: " + logfile.Length.ToString());
                    if (offset != 0 && logfile.Length == offset) continue; // No new data in file
                    logFileStream = new FileStream(logfile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    logFileReader = new StreamReader(logFileStream);
                    logFileReader.BaseStream.Seek(offset, SeekOrigin.Begin);

                    while (!logFileReader.EndOfStream)
                    {
                        line = logFileReader.ReadLine();
                        if (line.Trim().Length == 0)
                        {
                            continue;
                        }

                        //line = line.Remove(0, 1);
                        if (line.Length < 23) continue;
                        DateTime utcTimeOfLine = DateTime.MinValue;
                        if (!DateTime.TryParse(line.Substring(2, 19), out utcTimeOfLine)) continue;
                        Double minutesFromNow = Math.Abs(DateTime.UtcNow.Subtract(utcTimeOfLine).TotalMinutes);
                        if (minutesFromNow > 2) continue;
                        appendText(line);

                        //report function to send to webserver
                        ReportMsg(line);


                        LastMsgReported = DateTime.Now;
                    }
                    offset = logfile.Length;
                    if (fileToOffset.ContainsKey(logfile)) fileToOffset[logfile] = offset;
                    else fileToOffset.Add(logfile, offset);

                    // Clean up
                    logFileReader.Close();
                    logFileStream.Close();
                } // foreach
            } // try
            catch (Exception ex)
            {
                appendText(string.Format("Intel Server Error: {0}\r\n", ex.Message));
            }
            finally { Monitor.Exit(readerLock); }
        }

        public void ReportMsg(string line)
        {
            try
            {
                if (line.Contains("EVE System > Channel MOTD:")) return;

                //Try to only catch and send commands, thus the line needs to have ! in it
                if (line.Contains("!"))
                {
                    //TODO consider not clipping out here but on server side, as it's easier to support new prefix/commands
                    //Send line to server, let server figure out what it is. But clip out time, command and player
                    line = line.TrimStart('[').TrimStart(' ');
                    var msgTime = DateTime.Parse(line.Split(']')[0].TrimEnd(']').Trim());
                    var player = line.Remove(0, 22).Split('>')[0].Trim();

                    var index = line.IndexOf('!');
                    var command = line.Substring(index);
                    command = command.Substring(0, command.IndexOf(" "));

                    var cmd = new ReportCommand() {command = command, playerName = player, postedEveTime = msgTime};
                    var jsonMsg = JsonConvert.SerializeObject(cmd);

                    // TODO send to server
                    Encoding myEncoding = System.Text.UTF8Encoding.UTF8;
                    WebClient client = new WebClient();

                    //byte[] KiuResponse = client.UploadData(ReportServer, "PUT", myEncoding.GetBytes(jsonMsg));
                    //
                    //if (myEncoding.GetString(KiuResponse) == "OK\n") reported++;


                }
                else
                    return;
            }
            catch (Exception ex)
            {
                //failed
            }


        }

        private void MsgReportText_TextChanged(object sender, TextChangedEventArgs e)
        {
            MsgReportText.ScrollToEnd();
        }



        // Setup timers for control of how often to check files for updates
        // Setup paths and folders
        // Setup States

        // States: Idle, Running, Downtime


        // Check if eve is running, else go in idle state
        // only if msg have come since last update tick (xx millisecs) send it
        // Check for keywords "!recruiter" 
        // Issue: Sync between multiple ppl running program. Only want one notification
        // Maybe have an webapp that collects all times and stores them in db of "msg" and "time". 
        // If same msg/command already exists in database do not send it to discord, else do. 

        // webapp can be hosted on linux though laravel. Basic UI locked for admins to see all commands from last xx time. Just provide the service

        // Sends msg to discord of like "!recruiter" becomes = "@Recruiter playername asks for an recruiter in public chat"

    }
}
