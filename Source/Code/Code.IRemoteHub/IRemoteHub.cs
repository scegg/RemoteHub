﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SecretNest.RemoteHub
{
    /// <summary>
    /// Represents the base, non-generic version of the generic <see cref="IRemoteHub{T}"/>.
    /// </summary>
    public interface IRemoteHub
    {
        /// <summary>
        /// Occurs while an connection related exception is thrown.
        /// </summary>
        event EventHandler<ConnectionExceptionEventArgs> ConnectionErrorOccurred;

        /// <summary>
        /// Occurs while a remote client is added or changed virtual host setting.
        /// </summary>
        event EventHandler<ClientWithVirtualHostSettingEventArgs> RemoteClientUpdated;

        /// <summary>
        /// Occurs while a remote client is removed.
        /// </summary>
        event EventHandler<ClientIdEventArgs> RemoteClientRemoved;

        /// <summary>
        /// Occurs when this instance started.
        /// </summary>
        event EventHandler Started;

        /// <summary>
        /// Occurs when this instance stopped. Also will be raised if the instance is stopped by the request from underlying object and remote site.
        /// </summary>
        event EventHandler Stopped;

        /// <summary>
        /// Gets the client id.
        /// </summary>
        Guid ClientId { get; }

        /// <summary>
        /// Applies the virtual hosts setting for current client.
        /// </summary>
        /// <param name="settings">Virtual host settings. Key is virtual host id; Value is setting related to the virtual host specified.</param>
        void ApplyVirtualHosts(params KeyValuePair<Guid, VirtualHostSetting>[] settings);

        /// <summary>
        /// Gets the virtual host settings of the client specified by id.
        /// </summary>
        /// <param name="clientId">Client to be queried.</param>
        /// <param name="settings">Virtual host settings of the client specified. <see langword="null"/> if no setting applied on this client.</param>
        /// <returns>Whether the client is found.</returns>
        bool TryGetVirtualHosts(Guid clientId, out KeyValuePair<Guid, VirtualHostSetting>[] settings);

        /// <summary>
        /// Tries to resolve virtual host by id to host id.
        /// </summary>
        /// <param name="virtualHostId">Virtual host id.</param>
        /// <param name="clientId">Client id as the result.</param>
        /// <returns>Whether the resolving is succeeded or not.</returns>
        bool TryResolveVirtualHost(Guid virtualHostId, out Guid clientId);


        /// <summary>
        /// Starts instance operating.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops instance operating.
        /// </summary>
        void Stop();

        /// <summary>
        /// Gets whether this instance is started or not.
        /// </summary>
        bool IsStarted { get; }
    }




    /// <summary>
    /// Represents the methods, properties and event of RemoteHub.
    /// </summary>
    /// <typeparam name="T">Type of the message data.</typeparam>
    public interface IRemoteHub<T> : IRemoteHub
    {
        /// <summary>
        /// Sends a message to the a client specified by id.
        /// </summary>
        /// <param name="clientId">Client id.</param>
        /// <param name="message">Message to be sent.</param>
        void SendMessage(Guid clientId, T message);

        /// <summary>
        /// Creates a task that sends a message to the a client specified by id.
        /// </summary>
        /// <param name="clientId">Client id.</param>
        /// <param name="message">Message to be sent.</param>
        /// <returns>A task that represents the sending job.</returns>
        Task SendMessageAsync(Guid clientId, T message);
    }
}
