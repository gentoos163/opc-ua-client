﻿// Copyright (c) Converter Systems LLC. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Workstation.ServiceModel.Ua
{
    /// <summary>
    /// A collection of items to be monitored by the OPC UA server.
    /// </summary>
    public class Subscription : ISubscription, INotifyPropertyChanged, IDisposable
    {
        private const uint PublishTimeoutHint = 120 * 1000; // 2 minutes
        private const uint DiagnosticsHint = (uint)DiagnosticFlags.None;
        private static readonly MetroLog.ILogger Log = MetroLog.LogManagerFactory.DefaultLogManager.GetLogger<Subscription>();
        private volatile bool isPublishing = false;
        private MonitoredItemCollection items = new MonitoredItemCollection();
        private IDisposable token;

        /// <summary>
        /// Initializes a new instance of the <see cref="Subscription"/> class.
        /// </summary>
        /// <param name="session">The session client.</param>
        /// <param name="publishingInterval">The publishing interval in milliseconds.</param>
        /// <param name="keepAliveCount">The number of PublishingIntervals before the server should return an empty Publish response.</param>
        /// <param name="lifetimeCount">The number of PublishingIntervals before the server should delete the subscription. Set '0' to use session's lifetime.</param>
        /// <param name="maxNotificationsPerPublish">The maximum number of notifications per publish request. Set '0' to use no limit.</param>
        /// <param name="priority">The priority assigned to subscription.</param>
        public Subscription(UaTcpSessionClient session, double publishingInterval = 1000f, uint keepAliveCount = 10, uint lifetimeCount = 0, uint maxNotificationsPerPublish = 0, byte priority = 0)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            this.Session = session;
            this.PublishingInterval = publishingInterval;
            this.KeepAliveCount = keepAliveCount;
            this.LifetimeCount = lifetimeCount;
            this.MaxNotificationsPerPublish = maxNotificationsPerPublish;
            this.MonitoredItems = new ReadOnlyCollection<MonitoredItem>(this.items);
            this.PropertyChanged += this.OnPropertyChanged;

            // fill MonitoredItems collection
            var typeInfo = this.GetType().GetTypeInfo();
            foreach (var propertyInfo in typeInfo.DeclaredProperties)
            {
                var itemAttribute = propertyInfo.GetCustomAttribute<MonitoredItemAttribute>();
                if (itemAttribute == null)
                {
                    continue;
                }

                var item = new MonitoredItem
                {
                    NodeId = !string.IsNullOrEmpty(itemAttribute.NodeId) ? NodeId.Parse(itemAttribute.NodeId) : null,
                    IndexRange = itemAttribute.IndexRange,
                    AttributeId = itemAttribute.AttributeId,
                    SamplingInterval = itemAttribute.SamplingInterval,
                    QueueSize = itemAttribute.QueueSize,
                    DiscardOldest = itemAttribute.DiscardOldest,
                    Property = propertyInfo,
                };
                if (itemAttribute.AttributeId == AttributeIds.Value && (itemAttribute.DataChangeTrigger != DataChangeTrigger.StatusValue || itemAttribute.DeadbandType != DeadbandType.None))
                {
                    item.Filter = new DataChangeFilter() { Trigger = itemAttribute.DataChangeTrigger, DeadbandType = (uint)itemAttribute.DeadbandType, DeadbandValue = itemAttribute.DeadbandValue };
                }
                else if (itemAttribute.AttributeId == AttributeIds.EventNotifier)
                {
                    item.Filter = new EventFilter() { SelectClauses = EventHelper.GetSelectClauses(propertyInfo.PropertyType) };
                }

                this.items.Add(item);
            }

            // subscribe to data change and event notifications.
            this.token = session.Subscribe(this);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Gets the publishing interval.
        /// </summary>
        public double PublishingInterval { get; }

        /// <summary>
        /// Gets the number of PublishingIntervals before the server should return an empty Publish response.
        /// </summary>
        public uint KeepAliveCount { get; }

        /// <summary>
        /// Gets the number of PublishingIntervals before the server should delete the subscription.
        /// </summary>
        public uint LifetimeCount { get; }

        /// <summary>
        /// Gets the maximum number of notifications per publish request.
        /// </summary>
        public uint MaxNotificationsPerPublish { get; }

        /// <summary>
        /// Gets the priority assigned to subscription.
        /// </summary>
        public byte Priority { get; }

        /// <summary>
        /// Gets the collection of items to monitor.
        /// </summary>
        public ReadOnlyCollection<MonitoredItem> MonitoredItems { get; }

        /// <summary>
        /// Gets the session with the server.
        /// </summary>
        public UaTcpSessionClient Session { get; }

        /// <summary>
        /// Gets the identifier assigned by the server.
        /// </summary>
        public uint Id { get; private set; }

        /// <summary>
        /// Disposes the subscription.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.token?.Dispose();
            }
        }

        /// <summary>
        /// Receive StateChanged message.
        /// </summary>
        /// <param name="state">The service's CommunicationState.</param>
        public void OnStateChanged(CommunicationState state)
        {
            if (state == CommunicationState.Opened)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        // create the subscription.
                        var subscriptionRequest = new CreateSubscriptionRequest
                        {
                            RequestedPublishingInterval = this.PublishingInterval,
                            RequestedMaxKeepAliveCount = this.KeepAliveCount,
                            RequestedLifetimeCount = this.LifetimeCount > 0 ? this.LifetimeCount : (uint)(this.Session.SessionTimeout / this.PublishingInterval),
                            PublishingEnabled = false, // initially
                            Priority = this.Priority
                        };
                        var subscriptionResponse = await this.Session.CreateSubscriptionAsync(subscriptionRequest);
                        var id = this.Id = subscriptionResponse.SubscriptionId;

                        // add the items.
                        if (this.MonitoredItems.Count > 0)
                        {
                            var items = this.MonitoredItems.ToList();
                            var requests = items.Select(m => new MonitoredItemCreateRequest { ItemToMonitor = new ReadValueId { NodeId = m.NodeId, AttributeId = m.AttributeId, IndexRange = m.IndexRange }, MonitoringMode = m.MonitoringMode, RequestedParameters = new MonitoringParameters { ClientHandle = m.ClientId, DiscardOldest = m.DiscardOldest, QueueSize = m.QueueSize, SamplingInterval = m.SamplingInterval, Filter = m.Filter } }).ToArray();
                            var itemsRequest = new CreateMonitoredItemsRequest
                            {
                                SubscriptionId = id,
                                ItemsToCreate = requests,
                            };
                            var itemsResponse = await this.Session.CreateMonitoredItemsAsync(itemsRequest);
                            for (int i = 0; i < itemsResponse.Results.Length; i++)
                            {
                                var item = items[i];
                                var result = itemsResponse.Results[i];
                                item.ServerId = result.MonitoredItemId;
                                if (StatusCode.IsBad(result.StatusCode))
                                {
                                    Log.Warn($"Error response from MonitoredItemCreateRequest for {item.NodeId}. {result.StatusCode}");
                                }
                            }
                        }

                        // start publishing.
                        var modeRequest = new SetPublishingModeRequest
                        {
                            SubscriptionIds = new[] { id },
                            PublishingEnabled = true,
                        };
                        var modeResponse = await this.Session.SetPublishingModeAsync(modeRequest);
                    }
                    catch (ServiceResultException ex)
                    {
                        Log.Warn($"Error creating subscription '{this.GetType().Name}'. {ex.Message}");
                    }
                });
            }
        }

        /// <summary>
        /// Receive PublishResponse message.
        /// </summary>
        /// <param name="response">The publish response.</param>
        public void OnPublishResponse(PublishResponse response)
        {
            if (response.SubscriptionId != this.Id)
            {
                return;
            }

            try
            {
                this.isPublishing = true;

                // loop thru all the notifications
                var nd = response.NotificationMessage.NotificationData;
                foreach (var n in nd)
                {
                    // if data change.
                    var dcn = n as DataChangeNotification;
                    if (dcn != null)
                    {
                        MonitoredItem item;
                        foreach (var min in dcn.MonitoredItems)
                        {
                            if (this.items.TryGetValueByClientId(min.ClientHandle, out item))
                            {
                                item.Publish(this, min.Value);
                            }
                        }

                        continue;
                    }

                    // if event.
                    var enl = n as EventNotificationList;
                    if (enl != null)
                    {
                        MonitoredItem item;
                        foreach (var efl in enl.Events)
                        {
                            if (this.items.TryGetValueByClientId(efl.ClientHandle, out item))
                            {
                                item.Publish(this, efl.EventFields);
                            }
                        }
                    }
                }
            }
            finally
            {
                this.isPublishing = false;
                response.MoreNotifications = true; // set flag indicates message handled.
            }
        }

        protected virtual bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            this.NotifyPropertyChanged(propertyName);
            return true;
        }

        protected virtual void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Handles PropertyChanged event. If the property is associated with a MonitoredItem, then writes the property value to the node of the server.
        /// </summary>
        /// <param name="sender">the sender.</param>
        /// <param name="e">the event.</param>
        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (this.isPublishing || string.IsNullOrEmpty(e.PropertyName))
            {
                return;
            }

            MonitoredItem item;
            if (this.Session != null && this.items.TryGetValueByName(e.PropertyName, out item))
            {
                var pi = item.Property;
                if (pi != null && pi.CanRead)
                {
                    var value = pi.GetValue(sender);
                    this.Session.WriteAsync(new WriteRequest { NodesToWrite = new[] { new WriteValue { NodeId = item.NodeId, AttributeId = item.AttributeId, IndexRange = item.IndexRange, Value = value as DataValue ?? new DataValue(value) } } })
                        .ContinueWith(
                            t =>
                            {
                                foreach (var ex in t.Exception.InnerExceptions)
                                {
                                    Log.Warn($"Error writing value for NodeId {item.NodeId}. {ex.Message}");
                                }
                            }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }
    }
}