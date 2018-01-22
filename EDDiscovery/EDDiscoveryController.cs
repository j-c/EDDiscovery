﻿/*
 * Copyright © 2015 - 2017 EDDiscovery development team
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under
 * the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
 * ANY KIND, either express or implied. See the License for the specific language
 * governing permissions and limitations under the License.
 *
 * EDDiscovery is not affiliated with Frontier Developments plc.
 */

using EliteDangerousCore.EDSM;
using EliteDangerousCore.EDDN;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BaseUtils;
using EliteDangerousCore;
using EliteDangerousCore.DB;

namespace EDDiscovery
{
    public class EDDiscoveryController : IDiscoveryController
    {
        #region Public Interface
        #region Variables
        public HistoryList history { get; private set; } = new HistoryList();
        public EDSMSync EdsmSync { get; private set; }
        public EDSMLogFetcher EdsmLogFetcher { get; private set; }
        public string LogText { get { return logtext; } }
        public bool PendingClose { get; private set; }           // we want to close boys!
        public GalacticMapping galacticMapping { get; private set; }
        public bool ReadyForFinalClose { get; private set; }
        #endregion

        #region Events

        // IN ORDER OF CALLING DURING A REFRESH

        public event Action OnRefreshStarting;                              // UI. Called before worker thread starts, processing history (EDDiscoveryForm uses this to disable buttons and action refreshstart)
        public event Action OnRefreshCommanders;                            // UI. Called when refresh worker completes before final history is made (EDDiscoveryForm uses this to refresh commander stuff).  History is not valid here.
                                                                            // ALSO called if Loadgame is received.

        public event Action<HistoryList> OnHistoryChange;                   // UI. MAJOR. UC. Mirrored. Called AFTER history is complete, or via RefreshDisplays if a forced refresh is needed.  UC's use this
        public event Action OnRefreshComplete;                              // UI. Called AFTER history is complete.. Form uses this to know the whole process is over, and buttons may be turned on, actions may be run, etc
        public event Action OnInitialisationComplete;                       // UI.  Called AFTER first initial history load only

        // DURING A new Journal entry by the monitor, in order..

        public event Action<string, bool> OnNewUIEvent;                     // UI. MAJOR. UC. Mirrored. Always called irrespective of commander

                                                                            // Next two ONLY called if its for the current commander, and its not a screened out event (uievent)
        public event Action<HistoryEntry, HistoryList> OnNewEntry;          // UI. MAJOR. UC. Mirrored. Current commander. Called before OnNewJournalEntry, when NewEntry is called with a new item for the CURRENT commander
        public event Action<HistoryEntry, HistoryList> OnNewEntrySecond;    // UI. Current commander. After onNewEntry, Use if you want to do something after the main UI has been updated

        // If a UC is a Cursor Control type, then OnNewEntry should also fire the cursor control OnChangedSelection, OnTravelSelectionChanged after onNewEntry has been received by the cursor UC

        public event Action<JournalEntry> OnNewJournalEntry;                // UI. MAJOR. UC. Mirrored. Called after OnNewEntry, and when ANY new journal entry is created by the journal monitor

        // IF a log print occurs

        public event Action<string, Color> OnNewLogEntry;                   // UI. MAJOR. UC. Mirrored. New log entry generated.

        // During a Close

        public event Action OnBgSafeClose;                                  // BK. Background close, in BCK thread
        public event Action OnFinalClose;                                   // UI. Final close, in UI thread

        // During SYNC events and on start up

        public event Action OnInitialSyncComplete;                          // UI. Called during startup after CheckSystems done.
        public event Action OnSyncStarting;                                 // BK. EDSM/EDDB sync starting
        public event Action OnSyncComplete;                                 // BK. SYNC has completed
        public event Action<int, string> OnReportProgress;                  // UI. SYNC progress reporter

        #endregion

        #region Private vars
        private Queue<JournalEntry> journalqueue = new Queue<JournalEntry>();
        private System.Threading.Timer journalqueuedelaytimer;

        #endregion

        #region Initialisation

        public EDDiscoveryController(Func<Color> getNormalTextColor, Func<Color> getHighlightTextColor, Func<Color> getSuccessTextColor, Action<Action> invokeAsyncOnUiThread)
        {
            GetNormalTextColour = getNormalTextColor;
            GetHighlightTextColour = getHighlightTextColor;
            GetSuccessTextColour = getSuccessTextColor;
            InvokeAsyncOnUiThread = invokeAsyncOnUiThread;
            journalqueuedelaytimer = new Timer(DelayPlay, null, Timeout.Infinite, Timeout.Infinite);
        }

        public static void Initialize(Action<string> msg)    // called from EDDApplicationContext to initialize config and dbs
        {
            msg.Invoke("Checking Config");
            InitializeConfig();

            Trace.WriteLine($"*** Elite Dangerous Discovery Initializing - {EDDOptions.Instance.VersionDisplayString}, Platform: {Environment.OSVersion.Platform.ToString()}");

            msg.Invoke("Scanning Memory Banks");
            InitializeDatabases();

            msg.Invoke("Locating Crew Members");
            EDDConfig.Instance.Update(false);
        }

        public void Init()      // ED Discovery calls this during its init
        {
            if (!Debugger.IsAttached || EDDOptions.Instance.TraceLog)
            {
                TraceLog.LogFileWriterException += ex =>
                {
                    LogLineHighlight($"Log Writer Exception: {ex}");
                };
            }

            LoadIconPack();

            backgroundWorker = new Thread(BackgroundWorkerThread);
            backgroundWorker.IsBackground = true;
            backgroundWorker.Name = "Background Worker Thread";
            backgroundWorker.Start();

            galacticMapping = new GalacticMapping();

            EdsmSync = new EDSMSync(Logger);

            EdsmLogFetcher = new EDSMLogFetcher(LogLine);
            EdsmLogFetcher.OnDownloadedSystems += () => RefreshHistoryAsync();

            journalmonitor = new EDJournalClass(InvokeAsyncOnUiThread);
            journalmonitor.OnNewJournalEntry += NewEntry;
        }

        private void LoadIconPack()
        {
            Icons.IconSet.ResetIcons();

            string path = EDDOptions.Instance.IconsPath;

            if (path != null)
            {
                if (!Path.IsPathRooted(path))
                {
                    string testpath = Path.Combine(EDDOptions.Instance.AppDataDirectory, path);
                    if (File.Exists(testpath) || Directory.Exists(testpath))
                    {
                        path = testpath;
                    }
                    else
                    {
                        path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
                    }
                }

                if (Directory.Exists(path))
                {
                    Icons.IconSet.LoadIconsFromDirectory(path);
                }
                else if (File.Exists(path))
                {
                    try
                    {
                        Icons.IconSet.LoadIconsFromZipFile(path);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Unable to load icons from {path}: {ex.Message}");
                    }
                }
                else
                {
                    Trace.WriteLine($"Unable to load icons from {path}: Path not found");
                }
            }
        }

        public void Logger(string s)
        {
            LogLine(s);
        }

        public void PostInit_Loaded()
        {
            EDDConfig.Instance.Update();
        }

        public void PostInit_Shown()
        {
            readyForInitialLoad.Set();
        }

        public void InitComplete()
        {
            initComplete.Set();
        }
        #endregion

        #region Shutdown
        public void Shutdown()
        {
            if (!PendingClose)
            {
                PendingClose = true;
                EDDNSync.StopSync();
                EdsmSync.StopSync();
                EdsmLogFetcher.AsyncStop();
                journalmonitor.StopMonitor();
                LogLineHighlight("Closing down, please wait..");
                Console.WriteLine("Close.. safe close launched");
                closeRequested.Set();
                journalqueuedelaytimer.Change(Timeout.Infinite, Timeout.Infinite);
                journalqueuedelaytimer.Dispose();
            }
        }
        #endregion

        #region Logging
        public void LogLine(string text)
        {
            LogLineColor(text, GetNormalTextColour());
        }

        public void LogLineHighlight(string text)
        {
            TraceLog.WriteLine(text);
            LogLineColor(text, GetHighlightTextColour());
        }

        public void LogLineSuccess(string text)
        {
            LogLineColor(text, GetSuccessTextColour());
        }

        public void LogLineColor(string text, Color color)
        {
            try
            {
                InvokeAsyncOnUiThread(() =>
                {
                    logtext += text + Environment.NewLine;      // keep this, may be the only log showing

                    OnNewLogEntry?.Invoke(text + Environment.NewLine, color);
                });
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("******* Exception trying to write to ui thread log");
            }
        }

        public void ReportProgress(int percent, string message)
        {
            InvokeAsyncOnUiThread(() => OnReportProgress?.Invoke(percent, message));
        }
        #endregion

        #region History
        public bool RefreshHistoryAsync(string netlogpath = null, bool forcenetlogreload = false, bool forcejournalreload = false, int? currentcmdr = null)
        {
            if (PendingClose)
            {
                return false;
            }

            bool newrefresh = false;

            RefreshWorkerArgs curargs = refreshWorkerArgs;
            if (refreshRequestedFlag == 0)
            {
                if (curargs == null ||
                    curargs.ForceNetLogReload != forcenetlogreload ||
                    curargs.ForceJournalReload != forcejournalreload ||
                    curargs.CurrentCommander != (currentcmdr ?? history.CommanderId) ||
                    curargs.NetLogPath != netlogpath)
                {
                    newrefresh = true;
                }
            }

            if (Interlocked.CompareExchange(ref refreshRequestedFlag, 1, 0) == 0 || newrefresh)
            {
                refreshWorkerQueue.Enqueue(new RefreshWorkerArgs
                {
                    NetLogPath = netlogpath,
                    ForceNetLogReload = forcenetlogreload,
                    ForceJournalReload = forcejournalreload,
                    CurrentCommander = currentcmdr ?? history.CommanderId
                });

                refreshRequested.Set();
                return true;
            }
            else
            {
                return false;
            }
        }

        public void RefreshDisplays()
        {
            OnHistoryChange?.Invoke(history);
        }

        public void RecalculateHistoryDBs()         // call when you need to recalc the history dbs - not the whole history. Use RefreshAsync for that
        {
            history.ProcessUserHistoryListEntries(h => h.EntryOrder);

            RefreshDisplays();
        }
        #endregion

        #region EDSM / EDDB
        public bool AsyncPerformSync(bool eddbsync = false, bool edsmsync = false)
        {
            if (Interlocked.CompareExchange(ref resyncRequestedFlag, 1, 0) == 0)
            {
                OnSyncStarting?.Invoke();
                syncstate.perform_eddb_sync |= eddbsync;
                syncstate.perform_edsm_sync |= edsmsync;
                resyncRequestedEvent.Set();
                return true;
            }
            else
            {
                return false;
            }
        }
        #endregion

        #endregion  // MAJOR REGION

        #region Implementation
        #region Variables
        private string logtext = "";     // to keep in case of no logs..
        private event EventHandler HistoryRefreshed; // this is an internal hook

        private Task<bool> downloadMapsTask = null;

        private EDJournalClass journalmonitor;

        private ConcurrentQueue<RefreshWorkerArgs> refreshWorkerQueue = new ConcurrentQueue<RefreshWorkerArgs>();
        private EliteDangerousCore.EDSM.SystemClassEDSM.SystemsSyncState syncstate = new EliteDangerousCore.EDSM.SystemClassEDSM.SystemsSyncState();
        private RefreshWorkerArgs refreshWorkerArgs = new RefreshWorkerArgs();

        private Thread backgroundWorker;
        private Thread backgroundRefreshWorker;

        private ManualResetEvent closeRequested = new ManualResetEvent(false);
        private ManualResetEvent readyForInitialLoad = new ManualResetEvent(false);
        private ManualResetEvent initComplete = new ManualResetEvent(false);
        private ManualResetEvent readyForNewRefresh = new ManualResetEvent(false);
        private AutoResetEvent refreshRequested = new AutoResetEvent(false);
        private AutoResetEvent resyncRequestedEvent = new AutoResetEvent(false);
        private int refreshRequestedFlag = 0;
        private int resyncRequestedFlag = 0;
        #endregion

        #region Accessors
        private Func<Color> GetNormalTextColour;
        private Func<Color> GetHighlightTextColour;
        private Func<Color> GetSuccessTextColour;
        private Action<Action> InvokeAsyncOnUiThread;
        #endregion

        #region Initialization

        private static void InitializeDatabases()
        {
            Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Initializing database");
            SQLiteConnectionUser.Initialize();
            SQLiteConnectionSystem.Initialize();
            Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Database initialization complete");
        }

        private static void InitializeConfig()
        {
            EDDOptions.Instance.Init();

            string logpath = "";
            try
            {
                logpath = Path.Combine(EDDOptions.Instance.AppDataDirectory, "Log");
                if (!Directory.Exists(logpath))
                {
                    Directory.CreateDirectory(logpath);
                }

                TraceLog.logroot = EDDOptions.Instance.AppDataDirectory;
                TraceLog.urlfeedback = Properties.Resources.URLProjectFeedback;

                if (!Debugger.IsAttached || EDDOptions.Instance.TraceLog)
                {
                    TraceLog.Init();
                }

                if (EDDOptions.Instance.LogExceptions)
                {
                    TraceLog.RegisterFirstChanceExceptionHandler();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Unable to create the folder '{logpath}'");
                Trace.WriteLine($"Exception: {ex.Message}");
            }

            SQLiteConnectionUser.EarlyReadRegister();

            Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Init config finished");
        }

        #endregion

        #region 2dmaps

        public static Task<bool> DownloadMaps(IDiscoveryController discoveryform, Func<bool> cancelRequested, Action<string> logLine, Action<string> logError)          // ASYNC process
        {
            try
            {
                string mapsdir = Path.Combine(EDDOptions.Instance.AppDataDirectory, "Maps");
                if (!Directory.Exists(mapsdir))
                    Directory.CreateDirectory(mapsdir);

                logLine("Checking for new EDDiscovery maps");

                BaseUtils.GitHubClass github = new BaseUtils.GitHubClass(EDDiscovery.Properties.Resources.URLGithubDownload, discoveryform.LogLine);

                var files = github.GetDataFiles("Maps/V1");
                return Task.Factory.StartNew(() => github.DownloadFiles(files, mapsdir));
            }
            catch (Exception ex)
            {
                logError("DownloadImages exception: " + ex.Message);
                var tcs = new TaskCompletionSource<bool>();
                tcs.SetException(ex);
                return tcs.Task;
            }
        }

        #endregion

        #region Async EDSM/EDDB Full Sync

        //done after CheckSystems, in BackgroundInit

        private void DoPerformSync()
        {
            Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Perform sync");
            try
            {
                bool[] grids = new bool[GridId.MaxGridID];
                foreach (int i in GridId.FromString(EDDConfig.Instance.EDSMGridIDs))
                    grids[i] = true;

                ReportProgress(-1, "");

                syncstate.ClearCounters();

                if (syncstate.perform_edsm_sync || syncstate.perform_eddb_sync)
                {
                    if (syncstate.perform_edsm_sync && !PendingClose)
                    {
                        // Download new systems
                        try
                        {
                            syncstate.edsm_fullsync_count = SystemClassEDSM.PerformEDSMFullSync(grids, () => PendingClose, ReportProgress, LogLine, LogLineHighlight);
                            syncstate.perform_edsm_sync = false;
                        }
                        catch (Exception ex)
                        {
                            LogLineHighlight("GetAllEDSMSystems exception:" + ex.Message);
                        }

                    }

                    if (!PendingClose)
                    {
                        LogLine("Indexing systems table");
                        SQLiteConnectionSystem.CreateSystemsTableIndexes();

                        try
                        {
                            syncstate.eddb_sync_count = EliteDangerousCore.EDDB.SystemClassEDDB.PerformEDDBFullSync(()=>PendingClose, ReportProgress, LogLine, LogLineHighlight);
                            syncstate.perform_eddb_sync = false;
                        }
                        catch (Exception ex)
                        {
                            LogLineHighlight("GetEDDBUpdate exception: " + ex.Message);
                        }
                    }
                }

                if (!PendingClose)
                {
                    LogLine("Indexing systems table");
                    SQLiteConnectionSystem.CreateSystemsTableIndexes();

                    DateTime lastrecordtime = SystemClassEDSM.GetLastEDSMRecordTimeUTC();

                    if (DateTime.UtcNow.Subtract(lastrecordtime).TotalHours >= 1)  // If we have partial synced for 1 hour, do it..
                    {
                        LogLine("Checking for updated EDSM systems (may take a few moments).");
                        syncstate.edsm_updatesync_count = EliteDangerousCore.EDSM.SystemClassEDSM.PerformEDSMUpdateSync(grids, () => PendingClose, ReportProgress, LogLine, LogLineHighlight);
                    }
                }

                ReportProgress(-1, "");
            }
            catch (OperationCanceledException)
            {
                // Swallow Operation Cancelled exceptions
            }
            catch (Exception ex)
            {
                LogLineHighlight("Check Systems exception: " + ex.Message + Environment.NewLine + "Trace: " + ex.StackTrace);
            }

            InvokeAsyncOnUiThread(() => PerformSyncCompleted());
        }

        // Done in UI thread after DoPerformSync completes

        private void PerformSyncCompleted()    
        {
            ReportProgress(-1, "");

            if (!PendingClose)
            {
                long totalsystems = SystemClassDB.GetTotalSystems();
                LogLineSuccess($"Loading completed, total of {totalsystems:N0} systems");

                if (syncstate.edsm_fullsync_count > 0 || syncstate.eddb_sync_count > 0)   // if we have done a major resync
                {
                    LogLine("Refresh due to updating EDSM or EDDB data");
                    HistoryRefreshed += HistoryFinishedRefreshing;
                    RefreshHistoryAsync();
                }

                OnSyncComplete?.Invoke();

                resyncRequestedFlag = 0;
            }
            Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Perform sync completed");
        }

        private void HistoryFinishedRefreshing(object sender, EventArgs e)
        {
            HistoryRefreshed -= HistoryFinishedRefreshing;
            LogLine("Refreshing complete.");
            Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Refresh complete");

            if (syncstate.edsm_fullsync_count > 0 || syncstate.edsm_updatesync_count > 0)
                LogLine(string.Format("EDSM update complete with {0} systems", syncstate.edsm_fullsync_count + syncstate.edsm_updatesync_count));

            if (syncstate.eddb_sync_count > 0 )
                LogLine(string.Format("EDSM update complete with {0} systems", syncstate.eddb_sync_count));

            syncstate.ClearCounters();
        }

        #endregion

        #region Update Data

        protected class RefreshWorkerArgs
        {
            public string NetLogPath;
            public bool ForceNetLogReload;
            public bool ForceJournalReload;
            public int CurrentCommander;
        }

        private void DoRefreshHistory(RefreshWorkerArgs args)
        {
            HistoryList hist = null;
            try
            {
                refreshWorkerArgs = args;
                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Load history");
                hist = HistoryList.LoadHistory(journalmonitor, () => PendingClose, (p, s) => ReportProgress(p, $"Processing log file {s}"), args.NetLogPath, 
                    args.ForceJournalReload, args.ForceJournalReload, args.CurrentCommander , EDDConfig.Instance.ShowUIEvents );
                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Load history complete");
            }
            catch (Exception ex)
            {
                LogLineHighlight("History Refresh Error: " + ex);
            }

            initComplete.WaitOne();

            InvokeAsyncOnUiThread(() => RefreshHistoryWorkerCompleted(hist));
        }

        private void RefreshHistoryWorkerCompleted(HistoryList hist)
        {
            if (!PendingClose)
            {
                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Refresh history worker completed");

                if (hist != null)
                {
                    history.Copy(hist);

                    OnRefreshCommanders?.Invoke();

                    EdsmLogFetcher.StopCheck();


                    ReportProgress(-1, "");
                    LogLine("Refresh Complete.");

                    RefreshDisplays();
                    Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Refresh Displays Completed");
                }

                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " HR Refresh");

                HistoryRefreshed?.Invoke(this, EventArgs.Empty);        // Internal hook call

                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " JMOn");

                journalmonitor.StartMonitor();

                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " RFcomplete");
                OnRefreshComplete?.Invoke();                            // History is completed

                if (history.CommanderId >= 0)
                    EdsmLogFetcher.Start(EDCommander.Current);

                refreshRequestedFlag = 0;
                readyForNewRefresh.Set();

                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " refresh history complete");
            }
        }

        #endregion

        #region New Entry with merge

        public void NewEntry(JournalEntry je)        // on UI thread. hooked into journal monitor and receives new entries.. Also call if you programatically add an entry
        {
            Debug.Assert(System.Windows.Forms.Application.MessageLoop);

            int playdelay = HistoryList.MergeTypeDelay(je); // see if there is a delay needed..

            if (playdelay > 0)  // if delaying to see if a companion event occurs. add it to list. Set timer so we pick it up
            {
                System.Diagnostics.Debug.WriteLine(Environment.TickCount + " Delay Play queue " + je.EventTypeID + " Delay for " + playdelay);
                journalqueue.Enqueue(je);
                journalqueuedelaytimer.Change(playdelay, Timeout.Infinite);
            }
            else
            {
                journalqueuedelaytimer.Change(Timeout.Infinite, Timeout.Infinite);  // stop the timer, but if it occurs before this, not the end of the world
                journalqueue.Enqueue(je);  // add it to the play list.
                //System.Diagnostics.Debug.WriteLine(Environment.TickCount + " No delay, issue " + je.EventTypeID );
                PlayJournalList();    // and play
            }
        }

        public void PlayJournalList()                 // play delay list out..
        {
            Debug.Assert(System.Windows.Forms.Application.MessageLoop);
            //System.Diagnostics.Debug.WriteLine(Environment.TickCount + " Play out list");

            JournalEntry prev = null;  // we start afresh from the point of merging so we don't merge with previous ones already shown

            while( journalqueue.Count > 0 )
            {
                JournalEntry je = journalqueue.Dequeue();

                if (!HistoryList.MergeEntries(prev, je))                // if not merged
                {
                    if (prev != null)                       // no merge, so if we have a merge candidate on top, run actions on it.
                        ActionEntry(prev);

                    prev = je;                              // record
                }
            }

            if (prev != null)                               // any left.. action it
                ActionEntry(prev);
        }

        void ActionEntry(JournalEntry je)               // issue the JE to the system
        {
            if (je.IsUIEvent)            // give windows time to set up for OnNewEvent, and tell them if its coming via showuievents
            {
                if (je is EliteDangerousCore.JournalEvents.JournalMusic)
                    OnNewUIEvent?.Invoke((je as EliteDangerousCore.JournalEvents.JournalMusic).MusicTrack, EDDConfig.Instance.ShowUIEvents);
            }

            OnNewJournalEntry?.Invoke(je);          // Always call this on all entries...

            // filter out commanders, and filter out any UI events
            if (je.CommanderId == history.CommanderId && (!je.IsUIEvent || EDDConfig.Instance.ShowUIEvents))  
            {
                HistoryEntry he = history.AddJournalEntry(je, h => LogLineHighlight(h));        // add a new one on top
                //System.Diagnostics.Debug.WriteLine("Add HE " + he.EventSummary);
                OnNewEntry?.Invoke(he, history);            // major hook
                OnNewEntrySecond?.Invoke(he, history);      // secondary hook..
            }

            if (je.EventTypeID == JournalTypeEnum.LoadGame) // and issue this on Load game
            {
                OnRefreshCommanders?.Invoke();
            }
        }

        public void DelayPlay(Object s)             // timeout after play delay.. 
        {
            System.Diagnostics.Debug.WriteLine(Environment.TickCount + " Delay Play timer executed");
            journalqueuedelaytimer.Change(Timeout.Infinite, Timeout.Infinite);
            InvokeAsyncOnUiThread(() =>
            {
                PlayJournalList();
            });
        }

        #endregion

        #region Background Worker Threads
        private void BackgroundWorkerThread()
        {
            readyForInitialLoad.WaitOne();

            BackgroundInit();

            if (!PendingClose)
            {
                backgroundRefreshWorker = new Thread(BackgroundRefreshWorkerThread) { Name = "Background Refresh Worker", IsBackground = true };
                backgroundRefreshWorker.Start();

                try
                {
                    if (!EDDOptions.Instance.NoSystemsLoad && EDDConfig.Instance.EDSMEDDBDownload)      // if no system off, and EDSM download on
                        DoPerformSync();
                    else
                        LogLine("Star data download disabled by User, use Settings to reenable it");

                    while (!PendingClose)
                    {
                        int wh = WaitHandle.WaitAny(new WaitHandle[] { closeRequested, resyncRequestedEvent });

                        if (PendingClose) break;

                        switch (wh)
                        {
                            case 0:  // Close Requested
                                break;
                            case 1:  // Resync Requested
                                if (!EDDOptions.Instance.NoSystemsLoad && EDDConfig.Instance.EDSMEDDBDownload)      // if no system off, and EDSM download on
                                    DoPerformSync();
                                break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }

                backgroundRefreshWorker.Join();
            }

            closeRequested.WaitOne();

            OnBgSafeClose?.Invoke();
            ReadyForFinalClose = true;
            InvokeAsyncOnUiThread(() =>
            {
                OnFinalClose?.Invoke();
            });
        }

        // Called from Background Thread Worker at Init() 

        private void BackgroundInit()
        {
            StarScan.LoadBodyDesignationMap();
            MaterialCommodityDB.SetUpInitialTable();

            SQLiteConnectionSystem.CreateSystemsTableIndexes();     // just make sure they are there..

            Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Check systems");
            ReportProgress(-1, "");

            if (!EDDOptions.Instance.NoSystemsLoad)
            { 
                // Async load of maps in another thread

                downloadMapsTask = DownloadMaps(this, () => PendingClose, LogLine, LogLineHighlight);

                // Former CheckSystems, reworked to accomodate new switches..
                // Check to see what sync refreshes we need

                SystemClassEDSM.DetermineStartSyncState(syncstate);
                EliteDangerousCore.EDDB.SystemClassEDDB.DetermineStartSyncState(syncstate);

                // New Galmap load - it was not doing a refresh if EDSM sync kept on happening. Now has its own timer

                string rwgalmaptime = SQLiteConnectionSystem.GetSettingString("EDSMGalMapLast", "2000-01-01 00:00:00"); // Latest time from RW file.
                DateTime galmaptime;
                if (!DateTime.TryParse(rwgalmaptime, CultureInfo.InvariantCulture, DateTimeStyles.None, out galmaptime))
                    galmaptime = new DateTime(2000, 1, 1);

                if (DateTime.Now.Subtract(galmaptime).TotalDays > 14)  // Over 14 days do a sync from EDSM for galmap
                {
                    LogLine("Get galactic mapping from EDSM.");
                    galacticMapping.DownloadFromEDSM();
                    SQLiteConnectionSystem.PutSettingString("EDSMGalMapLast", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                }

                Debug.WriteLine(BaseUtils.AppTicks.TickCount100 + " Check systems complete");
            }

            galacticMapping.ParseData();                            // at this point, gal map data has been uploaded - get it into memory
            SystemClassDB.AddToAutoComplete(galacticMapping.GetGMONames());
            SystemNoteClass.GetAllSystemNotes();                             
            BookmarkClass.GetAllBookmarks();

            LogLine("Loaded Notes, Bookmarks and Galactic mapping.");

            ReportProgress(-1, "");
            InvokeAsyncOnUiThread(() => OnInitialSyncComplete?.Invoke());

            if (PendingClose) return;

            if (EliteDangerousCore.EDDN.EDDNClass.CheckforEDMC()) // EDMC is running
            {
                if (EDCommander.Current.SyncToEddn)  // Both EDD and EDMC should not sync to EDDN.
                {
                    LogLineHighlight("EDDiscovery and EDMarketConnector should not both sync to EDDN. Stop EDMC or uncheck 'send to EDDN' in settings tab!");
                }
            }

            if (PendingClose) return;
            LogLine("Reading travel history");

            if (!EDDOptions.Instance.NoLoad)
            {
                DoRefreshHistory(new RefreshWorkerArgs { CurrentCommander = EDCommander.CurrentCmdrID });
            }

            if (PendingClose) return;

            if (syncstate.perform_eddb_sync || syncstate.perform_edsm_sync)
            {
                string databases = (syncstate.perform_edsm_sync && syncstate.perform_eddb_sync) ? "EDSM and EDDB" : ((syncstate.perform_edsm_sync) ? "EDSM" : "EDDB");

                LogLine("ED Discovery will now synchronise to the " + databases + " databases to obtain star information." + Environment.NewLine +
                                "This will take a while, up to 15 minutes, please be patient." + Environment.NewLine +
                                "Please continue running ED Discovery until refresh is complete.");
            }

            InvokeAsyncOnUiThread(() => OnInitialisationComplete?.Invoke());
        }

        private void BackgroundRefreshWorkerThread()
        {
            WaitHandle.WaitAny(new WaitHandle[] { closeRequested, readyForNewRefresh }); // Wait to be ready for new refresh after initial refresh
            while (!PendingClose)
            {
                int wh = WaitHandle.WaitAny(new WaitHandle[] { closeRequested, refreshRequested });
                RefreshWorkerArgs argstemp = null;
                RefreshWorkerArgs args = null;

                if (PendingClose) break;

                switch (wh)
                {
                    case 0:  // Close Requested
                        break;
                    case 1:  // Refresh Requested
                        journalmonitor.StopMonitor();          // this is called by the foreground.  Ensure background is stopped.  Foreground must restart it.
                        EdsmLogFetcher.AsyncStop();     
                        InvokeAsyncOnUiThread(() =>
                        {
                            OnRefreshStarting?.Invoke();
                        });


                        while (refreshWorkerQueue.TryDequeue(out argstemp)) // Get the most recent refresh
                        {
                            args = argstemp;
                        }

                        if (args != null)
                        {
                            readyForNewRefresh.Reset();
                            DoRefreshHistory(args);
                            WaitHandle.WaitAny(new WaitHandle[] { closeRequested, readyForNewRefresh }); // Wait to be ready for new refresh
                        }
                        break;
                }
            }
        }

        #endregion

        #endregion

    }
}


