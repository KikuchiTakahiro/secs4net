﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Messaging;
using System.Net;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Lifetime;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using System.Windows.Forms;
using log4net;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Cim.Management;
using Secs4Net;
namespace Cim.Eap {
    sealed partial class HostMainForm : Form, IEAP {
        class SecsLogger : SecsTracer {
            public override void TraceMessageIn(SecsMessage msg, int systembyte) {
                EapLogger.Info(new SecsMessageLogInfo {
                    In = true,
                    Message = msg,
                    SystemByte = systembyte
                });
            }

            public override void TraceMessageOut(SecsMessage msg, int systembyte) {
                EapLogger.Info(new SecsMessageLogInfo {
                    In = false,
                    Message = msg,
                    SystemByte = systembyte
                });
            }

            public override void TraceInfo(string msg) {
                EapLogger.Info("SECS/GEM Info: " + msg);
            }

            public override void TraceWarning(string msg) {
                EapLogger.Warn("SECS/GEM Warning: " + msg);
            }

            public override void TraceError(string msg, Exception ex = null)
            {
                EapLogger.Error("SECS/GEM Error: " + msg);
            }
        }

        SecsGem _secsGem;
        readonly TextBoxAppender _screenLoger;
        readonly SecsTracer _secsLogger = new SecsLogger();

        public HostMainForm() {
            InitializeComponent();

            this.Text = ToolId + " EAP";
            this.eapDriverLabel.Text = EAPConfig.Instance.Driver.GetType().AssemblyQualifiedName;
            this.SecsMessages = new SecsMessageList(EAPConfig.Instance.SmlFile);
            this.EventReportLink = new DefineLinkConfig(EAPConfig.Instance.GemXml);

            listBoxSecsMessages.BeginUpdate();
            foreach (var msg in this.SecsMessages)
                listBoxSecsMessages.Items.Add(string.Format("{0,-8}: {1}", string.Format("S{0}F{1}", msg.S, msg.F), msg.Name));
            listBoxSecsMessages.EndUpdate();

            var l = (Logger)LogManager.GetLogger("EAP").Logger;
            l.AddAppender(
                _screenLoger = new TextBoxAppender(this.rtxtScreen) {
                    Layout = new PatternLayout("%date{MM/dd HH:mm:ss} %message%newline")
                });
        }

        protected override void OnLoad(EventArgs e) {
            base.OnLoad(e);

            EAPConfig.Instance.Driver.EAP = this;
            EAPConfig.Instance.Driver.Init();

            this.Subscribe(5, 1, "ToolAlarm", EAPConfig.Instance.Driver.HandleToolAlarm);

            reloadSpecialControlFileToolStripMenuItem_Click(this, EventArgs.Empty);
            menuItemGemEnable_Click(this, EventArgs.Empty);
        }

        protected override void OnClosed(EventArgs e) {
            EAPConfig.Instance.Driver.Unload();

            menuItemGemDisable_Click(this, EventArgs.Empty);
            base.OnClosed(e);
        }

        public override object InitializeLifetimeService() {
            return null;
        }

        #region UI Action
        void menuItemGemEnable_Click(object sender, EventArgs e) {
            EapLogger.Info("SECS/GEM Start");
            gemStatusLabel.Text = "Start";
            _secsGem?.Dispose();
            _secsGem = new SecsGem(
                IPAddress.Parse(EAPConfig.Instance.IP),
                EAPConfig.Instance.TcpPort,
                EAPConfig.Instance.Mode == ConnectionMode.Active, EAPConfig.Instance.SocketRecvBufferSize, _secsLogger, PrimaryMsgHandler)
            {
                DeviceId = EAPConfig.Instance.DeviceId,
                LinkTestInterval = EAPConfig.Instance.LinkTestInterval,
                T3 = EAPConfig.Instance.T3,
                T5 = EAPConfig.Instance.T5,
                T6 = EAPConfig.Instance.T6,
                T7 = EAPConfig.Instance.T7,
                T8 = EAPConfig.Instance.T8,
            };
            _secsGem.ConnectionChanged += delegate {
                this.Invoke((MethodInvoker)delegate {
                    EapLogger.Info("SECS/GEM " + _secsGem.State);
                    gemStatusLabel.Text = _secsGem.State.ToString();
                    eqpAddressStatusLabel.Text = "Device IP: " + _secsGem.DeviceAddress;
                    if (_secsGem.State == ConnectionState.Selected)
                        _secsGem.SendAsync(new SecsMessage(1, 13, "TestCommunicationsRequest", Item.L()));
                });
            };
            menuItemGemDisable.Enabled = true;
            menuItemGemEnable.Enabled = false;
        }

        private void PrimaryMsgHandler(SecsMessage primaryMsg, Action<SecsMessage> replySecondaryAcync)
        {
            try
            {
                replySecondaryAcync(SecsMessages[primaryMsg.S, (byte) (primaryMsg.F + 1)].FirstOrDefault());
                Action<SecsMessage> handler = null;
                if (_eventHandlers.TryGetValue(primaryMsg.GetKey(), out handler))
                    Parallel.ForEach(handler.GetInvocationList().Cast<Action<SecsMessage>>(), h => h(primaryMsg));
            }
            catch (Exception ex)
            {
                EapLogger.Error("Handle Primary SECS message Error", ex);
            }
        }

        void menuItemGemDisable_Click(object sender, EventArgs e) {
            if (_secsGem != null) {
                EapLogger.Info("SECS/GEM Stop");
                _secsGem.Dispose();
                _secsGem = null;
            }
            gemStatusLabel.Text = "Disable";
            menuItemGemDisable.Enabled = false;
            menuItemGemEnable.Enabled = true;
        }

        void menuItemSecsMessagestList_Click(object sender, EventArgs e) {
            bool showMsgList = ((ToolStripMenuItem)sender).Checked;
            splitContainer1.Panel1Collapsed = !showMsgList;
            _screenLoger.DisplaySecsMesssage = menuItemSecsTrace.Checked = showMsgList;
        }

        void menuItemEapConfig_Click(object sender, EventArgs e) {
            Process.Start("Notepad", AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
        }

        void menuItemGemConfig_Click(object sender, EventArgs e) {
            Process.Start("Notepad", @"..\config\Gem.xml");
        }

        void menuItemExit_Click(object sender, EventArgs e) {
            this.Close();
        }

        void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
            if (e.CloseReason == CloseReason.WindowsShutDown)
                return;
            e.Cancel = MessageBox.Show("你確定要關閉EAP嗎?", "關閉應用程式", MessageBoxButtons.YesNo) == DialogResult.No;
        }

        async void btnSend_Click(object sender, EventArgs e) {
            if (_secsGem == null) {
                MessageBox.Show("SECS/GEM not enable!");
                return;
            }
            try {
                EapLogger.Notice("Send by operator");
                await _secsGem.SendAsync(txtMsg.Text.ToSecsMessage());
            } catch (Exception ex) {
                EapLogger.Error(ex);
            }
        }

        void listBoxSecsMessageList_SelectedIndexChanged(object sender, EventArgs e) {
            SecsMessage secsMsg = SecsMessages[listBoxSecsMessages.SelectedIndex];
            using (var sw = new StringWriter()) {
                secsMsg.WriteTo(sw);
                txtMsg.Text = sw.ToString();
            }
        }

        void enableTraceLogToolStripMenuItem_Click(object sender, EventArgs e) {
            this._secsGem.LinkTestEnable = ((ToolStripMenuItem)sender).Checked;
        }

        void menuItemReloadGemXml_Click(object sender, EventArgs e) {
            try {
                this.EventReportLink = new DefineLinkConfig(EAPConfig.Instance.GemXml);
            } catch (Exception ex) {
                MessageBox.Show(ex.Message, "Reload gem.xml fail!!");
            }
        }

        void menuItemSecsTrace_Click(object sender, EventArgs e) {
            this._screenLoger.DisplaySecsMesssage = ((ToolStripMenuItem)sender).Checked;
        }

        async void defineLinkToolStripMenuItem_Click(object sender, EventArgs e) {
            try
            {
                await EAPConfig.Instance.Driver.DefineLink();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Define report link fail");
            }
        }

        void reloadSpecialControlFileToolStripMenuItem_Click(object sender, EventArgs e) {
            var c = (IConfigurable) EAPConfig.Instance.Driver;
            c?.LoadConfig();
        }

        void menuItemClearScreen_Click(object sender, EventArgs e) {
            this.rtxtScreen.Clear();
        }

        void enableLoggingToolStripMenuItem_Click(object sender, EventArgs e) {
        }

        void publishZServiceToolStripMenuItem_Click(object sender, EventArgs e) {
            Program.PublishZService();
        }
        #endregion

        #region ISecsDevice Members
        public string ToolId => EAPConfig.Instance.ToolId;

        static readonly HashSet<int> InvalidRemoteMessage = new HashSet<int> {
            1<<8 | 15, //s1F15 request offline
            1<<8 | 17, //s1f17 request online
            2<<8 | 33, //s2f33 define link
            2<<8 | 35, //s2f35 define link
            2<<8 | 37  //s2f37 define link
        };

        public Task<SecsMessage> SendAsync(SecsMessage msg) {
            if (_secsGem == null)
                throw new Exception("SECS/GEM not enable!");
            if (RemotingServices.IsTransparentProxy(msg) && InvalidRemoteMessage.Contains(msg.GetKey()))
                throw new ArgumentException("This message is not remotable!");
            return _secsGem.SendAsync(msg);
        }

        readonly ConcurrentDictionary<int, Action<SecsMessage>> _eventHandlers = new ConcurrentDictionary<int, Action<SecsMessage>>();
        readonly ConcurrentDictionary<string, Action<SecsMessage>> _recoverEventHandlers = new ConcurrentDictionary<string, Action<SecsMessage>>(StringComparer.Ordinal);

        IDisposable SubscribeLocal(SecsEventSubscription subscription) {
            var handler = new Action<SecsMessage>(msg => {
                #region event action
                var filter = subscription.Filter;
                try {
                    if (filter.Eval(msg.SecsItem)) {
                        EapLogger.Info("event[" + filter.Description + "] >> EAP");
                        msg.Name = filter.Name;
                        subscription.Handle(msg);
                    }
                } catch (Exception ex) {
                    EapLogger.Error("event[" + filter.Description + "] EAP process Error!", ex);
                }
                #endregion
            });
            _eventHandlers.AddHandler(subscription.GetKey(), handler);
            EapLogger.Notice("EAP subscribe event " + subscription.Filter.Description);
            return new LocalDisposable(() => {
                _eventHandlers.RemoveHandler(subscription.GetKey(), handler);
                EapLogger.Notice("EAP unsubscribe event " + subscription.Filter.Description);
            });
        }

        IDisposable SubscribeRemote(SecsEventSubscription subscription) {
            var filter = subscription.Filter;
            var description = filter.Description;
            var handler = new Action<SecsMessage>(msg => {
                #region event action
                try {
                    if (filter.Eval(msg.SecsItem)) {
                        EapLogger.Info("event[" + description + "] >> Z");
                        msg.Name = filter.Name;
                        subscription.Handle(msg);
                    }
                } catch (Exception ex) {
                    EapLogger.Error("event[" + description + "] Z process Error!", ex);
                }
                #endregion
            });
            int key = subscription.GetKey();
            string recoverQueuePath = @"FormatName:DIRECT=TCP:" + subscription.ClientAddress + @"\private$\" + subscription.Id;
            if (subscription.Recoverable) {
                return new SerializableDisposable(subscription,
                    new RemoteDisposable(subscription,
                        () => {
                            #region recover complete
                            _eventHandlers.AddHandler(key, handler);
                            EapLogger.Notice("Z subscribe event " + description);

                            Action<SecsMessage> recover = null;
                            if (_recoverEventHandlers.TryRemove(recoverQueuePath, out recover)) {
                                _eventHandlers.RemoveHandler(key, recover);
                                EapLogger.Notice("Z recover completely event[" + description + "]");
                            }
                            #endregion
                        },
                        () => {
                            #region Dispose
                            _eventHandlers.RemoveHandler(key, handler);
                            EapLogger.Notice("Z unsubscribe event " + description);

                            var queue = new MessageQueue(recoverQueuePath, QueueAccessMode.Send) {
                                Formatter = new BinaryMessageFormatter()
                            };
                            queue.DefaultPropertiesToSend.Recoverable = true;

                            var recover = new Action<SecsMessage>(msg => {
                                #region recover action
                                try {
                                    if (filter.Eval(msg.SecsItem)) {
                                        EapLogger.Info("recoverable event[" + description + "]");
                                        msg.Name = filter.Name;
                                        queue.Send(msg);
                                    }
                                } catch (Exception ex) {
                                    EapLogger.Error(ex);
                                }
                                #endregion
                            });
                            if (_recoverEventHandlers.TryAdd(recoverQueuePath, recover)) {
                                EapLogger.Notice("Z subscribe event[" + description + "] for recovering.");
                                _eventHandlers.AddHandler(key, recover);
                            }
                            #endregion
                        }));
            }
            _eventHandlers.AddHandler(key, handler);
            EapLogger.Notice("Z subscribe event " + description);
            return new SerializableDisposable(subscription,
                new RemoteDisposable(subscription, null, () => {
                    _eventHandlers.RemoveHandler(key, handler);
                    EapLogger.Notice("Z unsubscribe event " + description);
                }));
        }

        public IDisposable Subscribe(SecsEventSubscription subscription) {
            if (RemotingServices.IsTransparentProxy(subscription))
                return SubscribeRemote(subscription);
            return SubscribeLocal(subscription);
        }

        sealed class LocalDisposable : IDisposable {
            readonly Action _disposeAction;
            bool _isDisposed;
            internal LocalDisposable(Action disposeAction) {
                _disposeAction = disposeAction;
            }
            void IDisposable.Dispose() {
                if (!this._isDisposed) {
                    this._disposeAction();
                    this._isDisposed = true;
                }
            }
        }

        sealed class RemoteDisposable : MarshalByRefObject, IServerDisposable {
            static readonly ISponsor sponsor = new Sponsor();
            readonly SecsEventSubscription _subscription;
            readonly Action _subscribeAction;
            readonly Action _disposeAction;
            internal RemoteDisposable(SecsEventSubscription subscription, Action subscribeAction, Action disposeAction) {
                this._subscribeAction = subscribeAction;
                this._disposeAction = disposeAction;
                this._subscription = subscription;
                var lease = (ILease)subscription.GetLifetimeService();
                if (lease != null)
                    lease.Register(sponsor);
            }
#if DEBUG
            public override object InitializeLifetimeService() {
                ILease lease = (ILease)base.InitializeLifetimeService();
                if (lease.CurrentState == LeaseState.Initial) {
                    lease.InitialLeaseTime = TimeSpan.FromMinutes(20);
                    lease.RenewOnCallTime = TimeSpan.FromMinutes(20);
                    lease.SponsorshipTimeout = TimeSpan.FromMinutes(1);
                }
                return lease;
            }
#endif
            ~RemoteDisposable() {
                _subscription.Dispose();
                Trace.WriteLine("ServerDisposable destory!");
            }

            #region IDisposable Memebers

            volatile bool _isDisposed;

            [OneWay]
            public void Dispose() {
                if (!this._isDisposed) {
                    try {
                        this._disposeAction();
                        this._isDisposed = true;
                        var lease = (ILease)_subscription.GetLifetimeService();
                        if (lease != null) {
                            Trace.WriteLine("Unregister Subscrption.");
                            lease.Unregister(sponsor);
                        }
                    } catch {
                    }
                }
            }
            #endregion
            #region IServerDisposable Members
            [OneWay]
            public void RecoverComplete() {
                if (_subscribeAction != null)
                    _subscribeAction();
            }
            #endregion
        }
        #endregion

        #region IEAP Members
        public SecsMessageList SecsMessages { get; private set; }
        public DefineLinkConfig EventReportLink { get; private set; }
        #endregion

    }
}
