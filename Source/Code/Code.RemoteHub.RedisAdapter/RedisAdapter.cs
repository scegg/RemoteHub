﻿using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SecretNest.RemoteHub
{
    /// <summary>
    /// Represents the base, non-generic version of the generic <see cref="RedisAdapter{T}"/>, which converts RemoteHub commands and events to Redis database.
    /// </summary>
    public abstract class RedisAdapter : IDisposable, IRemoteHubRedisAdapter
    {
        ConnectionMultiplexer redisConnection;
        readonly RedisChannel mainChannel;
        IDatabase redisDatabase;
        ISubscriber publisher, subscriber;
        ConcurrentDictionary<Guid, ClientEntity> clients = new ConcurrentDictionary<Guid, ClientEntity>();
        ConcurrentDictionary<RedisChannel, Guid> targets = new ConcurrentDictionary<RedisChannel, Guid>(); //listening channel and it's client id
        readonly string redisConfiguration, privateChannelNamePrefix;
        readonly int redisDb, clientTimeToLive, clientRefreshingInterval;
        ClientTable clientTable;
        bool needRefreshFull = false;
        CancellationTokenSource updatingRedisCancellation, updatingRedisWaitingCancellation;
        CancellationToken updatingToken;
        Task updatingRedis;
        bool sendingNormal = false;
        bool isStopping = false;
        readonly ManualResetEventSlim startingLock = new ManualResetEventSlim();

        /// <inheritdoc/>
        public event EventHandler<ConnectionExceptionEventArgs> ConnectionErrorOccurred;
        /// <inheritdoc/>
        public event EventHandler<ClientWithVirtualHostSettingEventArgs> RemoteClientUpdated;
        /// <inheritdoc/>
        public event EventHandler<ClientIdEventArgs> RemoteClientRemoved;
        /// <inheritdoc/>
        public event EventHandler AdapterStarted;
        /// <inheritdoc/>
        public event EventHandler AdapterStopped;

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        /// <summary>
        /// Disposes of the resources (other than memory) used by this instance.
        /// </summary>
        /// <param name="disposing">True: release both managed and unmanaged resources; False: release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                StopConnection();

                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~RedisAdapter() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        /// <summary>
        /// Releases all resources used by this instance.
        /// </summary>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }

        #endregion IDisposable Support

        /// <summary>
        /// Initializes an instance of RedisAdapter.
        /// </summary>
        /// <param name="redisConfiguration">The string configuration to use for Redis multiplexer.</param>
        /// <param name="mainChannelName">Main channel name.</param>
        /// <param name="privateChannelNamePrefix">Prefix in naming of the private channel.</param>
        /// <param name="redisDb">The id to get a database for. Used in getting Redis database.</param>
        /// <param name="clientTimeToLive">Time to live (TTL) value of the host in seconds. Any records of hosts expired will be removed.</param>
        /// <param name="clientRefreshingInterval">Interval between refresh command sending operations in seconds.</param>
        protected RedisAdapter(string redisConfiguration, string mainChannelName, string privateChannelNamePrefix, int redisDb, int clientTimeToLive, int clientRefreshingInterval)
        {
            this.redisConfiguration = redisConfiguration;
            this.privateChannelNamePrefix = privateChannelNamePrefix;
            this.redisDb = redisDb;
            mainChannel = new RedisChannel(mainChannelName, RedisChannel.PatternMode.Literal);
            this.clientTimeToLive = clientTimeToLive;
            this.clientRefreshingInterval = clientRefreshingInterval;
            clientTable = new ClientTable(privateChannelNamePrefix);
        }

        RedisChannel BuildRedisChannel(Guid id)
        {
            return BuildRedisChannel(id.ToString("N"));
        }

        RedisChannel BuildRedisChannel(string idText)
        {
            RedisChannel redisChannel = new RedisChannel(privateChannelNamePrefix + idText, RedisChannel.PatternMode.Literal);
            return redisChannel;
        }

        #region Start Stop

        void StartConnection()
        {
            lock (startingLock)
            {
                if (updatingRedis != null) return;
                sendingNormal = true;
                isStopping = false;

                redisConnection = ConnectionMultiplexer.Connect(redisConfiguration);
                redisDatabase = redisConnection.GetDatabase(redisDb);
                publisher = redisDatabase.Multiplexer.GetSubscriber();
                subscriber = redisDatabase.Multiplexer.GetSubscriber();
                subscriber.Subscribe(mainChannel, OnMainChannelReceived);

                foreach (var clientId in clients.Keys)
                {
                    RedisChannel redisChannel = new RedisChannel(privateChannelNamePrefix + clientId.ToString("N"), RedisChannel.PatternMode.Literal);
                    if (targets.TryAdd(redisChannel, clientId))
                        subscriber.Subscribe(redisChannel, OnPrivateChannelReceived);
                }

                updatingRedisCancellation = new CancellationTokenSource();
                updatingToken = updatingRedisCancellation.Token;
                updatingRedisWaitingCancellation = new CancellationTokenSource();

                startingLock.Reset();

                updatingRedis = Task.Run(async () => await UpdateRedisAsync());
                startingLock.Wait();
                AdapterStarted?.Invoke(this, EventArgs.Empty);
            }
        }

        void StopConnection()
        {
            if (isStopping) return;
            Guid[] allRemoteClients;
            lock (startingLock)
            {
                if (updatingRedis == null) return;
                if (isStopping) return;

                isStopping = true;

                RemoveAllClients();

                updatingRedisCancellation.Cancel();
                updatingRedis.Wait();
                updatingRedis = null;
                updatingRedisCancellation.Dispose();
                updatingRedisCancellation = null;
                updatingRedisWaitingCancellation.Dispose();
                updatingRedisWaitingCancellation = null;

                subscriber.UnsubscribeAll();

                redisConnection.Close();
                redisConnection.Dispose();
                redisDatabase = null;
                redisConnection = null;

                allRemoteClients = GetAllRemoteClients().ToArray();
                foreach (var remoteClientId in allRemoteClients)
                {
                    clientTable.Remove(remoteClientId);
                }

                clientTable = new ClientTable(privateChannelNamePrefix);
                clients = new ConcurrentDictionary<Guid, ClientEntity>();
                targets = new ConcurrentDictionary<RedisChannel, Guid>();
                AdapterStopped?.Invoke(this, EventArgs.Empty);
            }
            if (RemoteClientRemoved != null)
                foreach (var remoteClientId in allRemoteClients)
                {
                    RemoteClientRemoved(this, new ClientIdEventArgs(remoteClientId));
                }
        }

        /// <inheritdoc/>
        public void Start()
        {
            StartConnection();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            StopConnection();
        }

        /// <inheritdoc/>
        public bool IsStarted => updatingRedis != null;

        #endregion Start Stop

        #region Main Channel Operating

        void OnMainChannelReceived(RedisChannel channel, RedisValue value)
        {
            var texts = ((string)value).Split(':');
            if (texts[0] == "v1")
            {
                if (texts[1] == "Refresh")
                {
                    var clientId = Guid.Parse(texts[2]);
                    var seconds = int.Parse(texts[3]);
                    clientTable.AddOrRefresh(clientId, seconds, out var currentVirtualHostSettingId, out bool isNewCreated);
                    if (texts[4] != "")
                    {
                        Guid virtualHostId = Guid.Parse(texts[4]);
                        if (currentVirtualHostSettingId != virtualHostId)
                        {
                            //new created with having-virtual-host
                            MainChannelPublishing("v1:NeedRefreshFull:" + texts[2]);
                            //delay sending RemoteClientUpdated until RefreshFull received.
                        }
                    }
                    else
                    {
                        if (currentVirtualHostSettingId != Guid.Empty)
                        {
                            //From having-virtual-host state to no-virtual-host state
                            clientTable.ClearVirtualHosts(clientId);
                            if (RemoteClientUpdated != null && !IsSelf(clientId))
                            {
                                RemoteClientUpdated(this, new ClientWithVirtualHostSettingEventArgs(clientId, Guid.Empty, null));
                            }
                        }
                        else if (isNewCreated)
                        {
                            //new created with no-virtual-host
                            if (RemoteClientUpdated != null && !IsSelf(clientId))
                            {
                                RemoteClientUpdated(this, new ClientWithVirtualHostSettingEventArgs(clientId, Guid.Empty, null));
                            }
                        }
                    }
                }
                else if (texts[1] == "RefreshFull")
                {
                    var clientId = Guid.Parse(texts[2]);
                    var seconds = int.Parse(texts[3]);
                    clientTable.AddOrRefresh(clientId, seconds, out var currentVirtualHostSettingId, out bool isNewCreated);
                    if (texts[4] != "")
                    {
                        Guid virtualHostId = Guid.Parse(texts[4]);
                        if (currentVirtualHostSettingId != virtualHostId)
                        {
                            //From no-virtual-host state to having-virtual-host state, or change virtual host setting
                            var setting = clientTable.ApplyVirtualHosts(clientId, virtualHostId, texts[5]);
                            if (RemoteClientUpdated != null && !IsSelf(clientId))
                            {
                                RemoteClientUpdated(this, new ClientWithVirtualHostSettingEventArgs(clientId, virtualHostId, setting.ToArray()));
                            }
                        }
                    }
                    else
                    {
                        if (currentVirtualHostSettingId != Guid.Empty)
                        {
                            clientTable.ClearVirtualHosts(clientId);
                            //From having-virtual-host state to no-virtual-host state
                            if (RemoteClientUpdated != null && !IsSelf(clientId))
                            {
                                RemoteClientUpdated(this, new ClientWithVirtualHostSettingEventArgs(clientId, Guid.Empty, null));
                            }
                        }
                        else if (isNewCreated)
                        {
                            //new created with no-virtual-host
                            if (RemoteClientUpdated != null && !IsSelf(clientId))
                            {
                                RemoteClientUpdated(this, new ClientWithVirtualHostSettingEventArgs(clientId, Guid.Empty, null));
                            }
                        }
                    }
                }
                else if (texts[1] == "Hello")
                {
                    needRefreshFull = true;
                    var old = updatingRedisWaitingCancellation;
                    updatingRedisWaitingCancellation = new CancellationTokenSource();
                    old.Cancel();
                }
                else if (texts[1] == "NeedRefreshFull")
                {
                    var clientId = Guid.Parse(texts[2]);
                    string refreshText;
                    if (clients.TryGetValue(clientId, out var client))
                    {
                        refreshText = client.CommandTextRefreshFull;
                    }
                    else
                    {
                        refreshText = null;
                    }
                    if (refreshText != null)
                    {
                        MainChannelPublishing(refreshText);
                    }
                }
                else if (texts[1] == "Shutdown")
                {
                    var clientId = Guid.Parse(texts[2]);
                    if (!IsSelf(clientId) && clientTable.Remove(clientId) && RemoteClientRemoved != null)
                    {
                        RemoteClientRemoved(this, new ClientIdEventArgs(clientId));
                    }
                }
            }
        }

        const string messageTextHello = "v1:Hello";
        readonly TimeSpan smallTimeFix = new TimeSpan(0, 0, 0, 0, 200); //help to make sure at least twice refreshes before record expired.
        async Task UpdateRedisAsync()
        {
            //start
            await MainChannelPublishingAsync(messageTextHello, updatingToken);  // This Hello message will also be received by the sender, which will cause the delay in the 1st round of keeping will be canceled.

            //started
            var waitingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(updatingToken, updatingRedisWaitingCancellation.Token);
            try
            {
                var waitingToken = waitingTokenSource.Token;
                var nextRefresh = DateTime.Now.AddSeconds(clientRefreshingInterval);

                startingLock.Set();

                //keeping
                while (!updatingToken.IsCancellationRequested)
                {
                    try
                    {
                        TimeSpan delay = nextRefresh - DateTime.Now - smallTimeFix;
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, waitingToken);
                        nextRefresh = nextRefresh.AddSeconds(clientRefreshingInterval);
                    }
                    catch (TaskCanceledException)
                    {
                        if (!updatingToken.IsCancellationRequested)
                        {
                            nextRefresh = DateTime.Now.AddSeconds(clientRefreshingInterval);
                            var old = waitingTokenSource;
                            waitingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(updatingToken, updatingRedisWaitingCancellation.Token);
                            waitingToken = waitingTokenSource.Token;
                            old.Dispose();
                        }
                    }

                    clientTable.ClearAllExpired(out var expired);
                    if (RemoteClientRemoved != null)
                        foreach (var id in expired)
                        {
                            if (!IsSelf(id))
                                RemoteClientRemoved(this, new ClientIdEventArgs(id));
                        }

                    string[] refreshTexts;
                    if (needRefreshFull)
                    {
                        needRefreshFull = false;
                        refreshTexts = clients.Values.Select(i => i.CommandTextRefreshFull).ToArray();
                    }
                    else
                    {
                        refreshTexts = clients.Values.Select(i => i.CommandTextRefresh).ToArray();
                    }
                    if (refreshTexts.Length > 0)
                    {
                        try
                        {
                            Array.ForEach(refreshTexts, async i => await MainChannelPublishingAsync(i, updatingToken));

                        }
                        catch (TaskCanceledException) { }
                    }
                }
            }
            finally
            {
                waitingTokenSource.Dispose();
            }
        }

        void MainChannelPublishing(string text)
        {
            int retried = 0;
            while (sendingNormal)
            {
                try
                {
                    publisher.Publish(mainChannel, text);
                    return;
                }
                catch (RedisTimeoutException ex)
                {
                    if (retried > 3)
                    {
                        if (ConnectionErrorOccurred != null)
                        {
                            Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, false, true)));
                        }
                        break;
                    }
                    else
                    {
                        retried++;
                    }
                }
                catch (Exception ex) //RedisConnectionException
                {
                    sendingNormal = false;
                    if (ConnectionErrorOccurred != null)
                    {
                        Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, true, false)));
                    }
                    StopConnection();
                    break;
                }
            }
        }

        async Task MainChannelPublishingAsync(string text, CancellationToken cancellationToken)
        {
            int retried = 0;
            while (sendingNormal && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await publisher.PublishAsync(mainChannel, text);
                    return;
                }
                catch (RedisTimeoutException ex)
                {
                    if (retried > 3)
                    {
                        if (ConnectionErrorOccurred != null)
                        {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                            Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, false, true)));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        }
                        break;
                    }
                    else
                    {
                        retried++;
                    }
                }
                catch (Exception ex) //RedisConnectionException
                {
                    sendingNormal = false;
                    if (ConnectionErrorOccurred != null)
                    {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, true, false)));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    }
                    StopConnection();
                    break;
                }
            }
        }

        #endregion Main Channel Operating

        #region Private Channel Operating

        /// <summary>
        /// Will be called when a private message is received from the redis channel.
        /// </summary>
        /// <param name="targetClientId">Client id of the receiver.</param>
        /// <param name="value">Data of the message.</param>
        protected abstract void OnPrivateMessageReceived(Guid targetClientId, RedisValue value);

        void OnPrivateChannelReceived(RedisChannel channel, RedisValue value)
        {
            if (targets.TryGetValue(channel, out var clientId))
            {
                OnPrivateMessageReceived(clientId, value);
            }
        }

        /// <summary>
        /// Sends the private message.
        /// </summary>
        /// <param name="channel">Channel of the receiver.</param>
        /// <param name="value">Data of the message.</param>
        /// <returns>A task that represents the sending job.</returns>
        protected async Task SendPrivateMessageAsync(RedisChannel channel, RedisValue value)
        {
            if (!IsStarted)
                throw new InvalidOperationException();

            try
            {
                await publisher.PublishAsync(channel, value);
            }
            catch (RedisTimeoutException ex)
            {
                if (ConnectionErrorOccurred != null)
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, false, false)));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
                throw;
            }
            catch (Exception ex)
            {
                sendingNormal = false;
                if (ConnectionErrorOccurred != null)
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, true, false)));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
                StopConnection();
                throw;
            }
        }

        /// <summary>
        /// Sends the private message.
        /// </summary>
        /// <param name="targetClientId">Client id of the receiver.</param>
        /// <param name="value">Data of the message.</param>
        /// <returns>A task that represents the sending job.</returns>
        protected async Task SendPrivateMessageAsync(Guid targetClientId, RedisValue value)
        {
            if (!IsStarted)
                throw new InvalidOperationException();

            RedisChannel redisChannel = BuildRedisChannel(targetClientId);
            await SendPrivateMessageAsync(redisChannel, value);
        }

        /// <summary>
        /// Sends the private message.
        /// </summary>
        /// <param name="channel">Channel of the receiver.</param>
        /// <param name="value">Data of the message.</param>
        protected void SendPrivateMessage(RedisChannel channel, RedisValue value)
        {
            if (!IsStarted)
                throw new InvalidOperationException();

            try
            {
                publisher.Publish(channel, value);
            }
            catch (RedisTimeoutException ex)
            {
                if (ConnectionErrorOccurred != null)
                {
                    Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, false, false)));
                }
                throw;
            }
            catch (Exception ex)
            {
                sendingNormal = false;
                if (ConnectionErrorOccurred != null)
                {
                    Task.Run(() => ConnectionErrorOccurred(this, new ConnectionExceptionEventArgs(ex, true, false)));
                }
                StopConnection();
                throw;
            }
        }

        /// <summary>
        /// Sends the private message.
        /// </summary>
        /// <param name="targetClientId">Client id of the receiver.</param>
        /// <param name="value">Data of the message.</param>
        protected void SendPrivateMessage(Guid targetClientId, RedisValue value)
        {
            if (!IsStarted)
                throw new InvalidOperationException();

            RedisChannel redisChannel = BuildRedisChannel(targetClientId);
            SendPrivateMessage(redisChannel, value);
        }

        #endregion Private Channel Operating

        #region Client Id

        /// <summary>
        /// Gets whether the client specified is registered as local.
        /// </summary>
        /// <param name="clientId">Client to be checked.</param>
        /// <returns>Whether the client is registered as local.</returns>
        protected bool IsSelf(Guid clientId)
        {
            return clients.ContainsKey(clientId);
        }

        /// <summary>
        /// Gets whether the client specified by redis channel is registered as local. Output the client id if it is.
        /// </summary>
        /// <param name="redisChannel">Redis channel of the client to be checked.</param>
        /// <param name="clientId">The client id if it is the local. This is an out parameter.</param>
        /// <returns>Whether the client is registered as local.</returns>
        protected bool IsSelf(RedisChannel redisChannel, out Guid clientId)
        {
            return targets.TryGetValue(redisChannel, out clientId); //targets: contains all local listening channel and it's client id.
        }

        List<string> AddClientWithoutSendingRefresh(Guid[] clientId)
        {
            lock (startingLock)
            {
                List<string> refreshTexts = new List<string>();
                bool isStarted = IsStarted;
                List<RedisChannel> channelToBeSubscribed;
                if (isStarted)
                    channelToBeSubscribed = new List<RedisChannel>();
                else
                    channelToBeSubscribed = null;


                foreach (var id in clientId)
                {
                    string idText = id.ToString("N");
                    var client = new ClientEntity(string.Format("v1:Refresh:{0}:{1}:", idText, clientTimeToLive), string.Format("v1:RefreshFull:{0}:{1}:", idText, clientTimeToLive));
                    if (clients.TryAdd(id, client))
                    { 
                        if (isStarted)
                        {
                            RedisChannel redisChannel = BuildRedisChannel(idText);
                            if (targets.TryAdd(redisChannel, id))
                            {
                                channelToBeSubscribed.Add(redisChannel);
                            }
                            refreshTexts.Add(client.CommandTextRefreshFull);
                        }
                    }
                }
                if (isStarted)
                    channelToBeSubscribed.ForEach(i => subscriber.Subscribe(i, OnPrivateChannelReceived));
                return refreshTexts;
            }
        }

        string AddClientWithoutSendingRefresh(Guid clientId, KeyValuePair<Guid, VirtualHostSetting>[] settings)
        {
            lock (startingLock)
            {
                bool isStarted = IsStarted;
                string idText = clientId.ToString("N");
                var client = new ClientEntity(string.Format("v1:Refresh:{0}:{1}:", idText, clientTimeToLive), string.Format("v1:RefreshFull:{0}:{1}:", idText, clientTimeToLive));
                RedisChannel toSubscribe = default;
                bool needToSubsribe = false;
                string result = null;
                if (clients.TryAdd(clientId, client))
                {
                    if (settings != null && settings.Length > 0)
                    {
                        client.ApplyVirtualHostSetting(settings);
                    }
                    if (isStarted)
                    {
                        RedisChannel redisChannel = BuildRedisChannel(idText);
                        if (targets.TryAdd(redisChannel, clientId))
                        {
                            toSubscribe = redisChannel;
                            needToSubsribe = true;
                        }
                        result = client.CommandTextRefreshFull;
                    }
                }
                if (needToSubsribe)
                {
                    subscriber.Subscribe(toSubscribe, OnPrivateChannelReceived);
                }
                return result;
            }
        }

        /// <inheritdoc/>
        public async Task AddClientAsync(params Guid[] clientId)
        {
            try
            {
                var toRefresh = AddClientWithoutSendingRefresh(clientId);
                foreach(var text in toRefresh)
                {
                    await MainChannelPublishingAsync(text, updatingToken);
                }
            }
            catch (TaskCanceledException) { }
        }

        /// <inheritdoc/>
        public void AddClient(params Guid[] clientId)
        {
            AddClientWithoutSendingRefresh(clientId).ForEach(i => MainChannelPublishing(i));
        }

        async Task RemoveOneClientAsync(string idText)
        {
            //dont need to lock. no harm if removing while not started.
            bool isStarted = IsStarted;
            if (isStarted)
            {
                await MainChannelPublishingAsync(string.Format("v1:Shutdown:{0}", idText), updatingRedisCancellation.Token);
                RedisChannel redisChannel = BuildRedisChannel(idText);
                if (targets.TryRemove(redisChannel, out var clientId))
                {
                    clientTable.Remove(clientId);
                    await subscriber.UnsubscribeAsync(redisChannel, OnPrivateChannelReceived);
                }
            }
        }

        void RemoveOneClient(string idText)
        {
            //dont need to lock. no harm if removing while not started.
            bool isStarted = IsStarted;
            if (isStarted)
            {
                MainChannelPublishing(string.Format("v1:Shutdown:{0}", idText));
                RedisChannel redisChannel = BuildRedisChannel(idText);
                if (targets.TryRemove(redisChannel, out var clientId))
                {
                    clientTable.Remove(clientId);
                    subscriber.Unsubscribe(redisChannel, OnPrivateChannelReceived);
                }
            }
        }

        List<string> RemoveClientWithoutTargetNorRedisOperating(Guid[] clientId)
        {
            List<string> allIdText = new List<string>();
            foreach (var id in clientId)
            {
                if (clients.TryRemove(id, out _))
                {
                    allIdText.Add(id.ToString("N"));
                }
            }
            return allIdText;
        }

        /// <inheritdoc/>
        public async Task RemoveClientAsync(params Guid[] clientId)
        {
            List<string> allIdText = RemoveClientWithoutTargetNorRedisOperating(clientId);

            foreach(var idText in allIdText)
            {
                await RemoveOneClientAsync(idText);
            }
        }

        /// <inheritdoc/>
        public void RemoveClient(params Guid[] clientId)
        {
            List<string> allIdText = RemoveClientWithoutTargetNorRedisOperating(clientId);
            allIdText.ForEach(i => RemoveOneClient(i));
        }

        /// <inheritdoc/>
        public async Task RemoveAllClientsAsync()
        {
            string[] allIdText = Array.ConvertAll(clients.Keys.ToArray(), i => i.ToString("N"));

            foreach (var idText in allIdText)
            {
                await RemoveOneClientAsync(idText);
            }
        }

        /// <inheritdoc/>
        public void RemoveAllClients()
        {
            string[] allIdText = Array.ConvertAll(clients.Keys.ToArray(), i => i.ToString("N"));
            foreach (var idText in allIdText)
            {
                RemoveOneClient(idText);
            }
        }

        /// <inheritdoc/>
        public IEnumerable<Guid> GetAllClients()
        {
            Guid[] result = clients.Keys.ToArray();
            return result;
        }

        #endregion Client Id

        /// <inheritdoc/>
        public IEnumerable<Guid> GetAllRemoteClients()
        {
            HashSet<Guid> localClients = new HashSet<Guid>();
            foreach(var id in clients.Keys.ToArray())
            {
                localClients.Add(id);
            }
            Guid[] result = clientTable.GetAllClientsId().ToArray();
            foreach(var id in result)
            {
                if (localClients.Contains(id))
                    continue;
                else
                    yield return id;
            }
        }

        /// <inheritdoc/>
        public void ApplyVirtualHosts(Guid clientId, params KeyValuePair<Guid, VirtualHostSetting>[] settings)
        {
            string refreshText;

            if (clients.TryGetValue(clientId, out var client))
            {
                client.ApplyVirtualHostSetting(settings);
                refreshText = client.CommandTextRefreshFull;
            }
            else
            {
                refreshText = AddClientWithoutSendingRefresh(clientId, settings);
            }
            if (refreshText != null && IsStarted)
            {
                MainChannelPublishing(refreshText);
            }
        }

        /// <inheritdoc/>
        public bool TryResolve(Guid clientId, out RedisChannel channel)
        {
            var result = clientTable.TryGet(clientId, out channel, out var isTimedOutAndRemoved);
            if (isTimedOutAndRemoved && RemoteClientRemoved != null) //dont need to check IsSelf. local client will not trigger timeout.
            {
                RemoteClientRemoved(this, new ClientIdEventArgs(clientId));
            }
            return result;
        }

        /// <inheritdoc/>
        public bool TryResolveVirtualHost(Guid virtualHostId, out Guid clientId)
        {
            return clientTable.TryResolveVirtualHost(virtualHostId, out clientId);
        }

        /// <inheritdoc/>
        public bool TryGetVirtualHosts(Guid clientId, out KeyValuePair<Guid, VirtualHostSetting>[] settings)
        {
            var entity = clientTable.Get(clientId);
            if (entity == null)
            {
                settings = default;
                return false;
            }
            else
            {
                if (entity.IsVirtualHostsDisabled)
                {
                    settings = default;
                }
                else
                {
                    settings = entity.GetVirtualHosts();
                }
                return true;
            }
        }
    }
}