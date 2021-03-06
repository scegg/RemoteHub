﻿using System;
using System.Collections.Generic;
using System.Text;

namespace SecretNest.RemoteHub
{
    partial class ClientEntity
    {
        public List<Guid> ApplyVirtualHosts(Guid settingId, KeyValuePair<Guid, VirtualHostSetting>[] virtualHostSettings)
        {
            var affectedVirtualHosts = new List<Guid>();

            var newHosts = new Dictionary<Guid, VirtualHostSetting>();
            lock (virtualHostLock)
            {
                VirtualHostSettingId = settingId;
                foreach (var virtualHostSetting in virtualHostSettings)
                {
                    var virtualHostId = virtualHostSetting.Key;
                    var virtualHost = virtualHostSetting.Value;

                    newHosts.Add(virtualHostId, virtualHost);

                    if (VirtualHosts.TryGetValue(virtualHostId, out var oldVirtualHost))
                    {
                        if (oldVirtualHost != virtualHost)
                        {
                            affectedVirtualHosts.Add(virtualHostId);//changed
                        }
                        VirtualHosts.Remove(virtualHostId);
                    }
                    else
                    {
                        affectedVirtualHosts.Add(virtualHostId);//added
                    }
                }
                affectedVirtualHosts.AddRange(VirtualHosts.Keys);//removed
                VirtualHosts = newHosts;
            }
            return affectedVirtualHosts;
        }
    }
}
