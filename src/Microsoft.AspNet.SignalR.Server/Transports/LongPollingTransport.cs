// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.


using System;
using System.Threading.Tasks;
using Microsoft.AspNet.Hosting;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.SignalR.Infrastructure;
using Microsoft.AspNet.SignalR.Json;
using Microsoft.Framework.Logging;
using Microsoft.Framework.OptionsModel;
using Newtonsoft.Json;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.AspNet.SignalR.Transports
{
    public class LongPollingTransport : ForeverTransport, ITransport
    {
        private readonly TimeSpan _pollDelay;
        private bool _responseSent;

        private static readonly byte[] _keepAlive = new byte[] { 32 };

        public LongPollingTransport(HttpContext context,
                                    JsonSerializer jsonSerializer,
                                    ITransportHeartbeat heartbeat,
                                    IPerformanceCounterManager performanceCounterManager,
                                    IApplicationLifetime applicationLifetime,
                                    ILoggerFactory loggerFactory,
                                    IOptionsAccessor<SignalROptions> optionsAccessor,
                                    IMemoryPool pool)
            : base(context, jsonSerializer, heartbeat, performanceCounterManager, applicationLifetime, loggerFactory, pool)
        {
            _pollDelay = optionsAccessor.Options.Transports.LongPolling.PollDelay;
        }

        public override TimeSpan DisconnectThreshold
        {
            get { return _pollDelay; }
        }

        private bool IsJsonp
        {
            get
            {
                return !String.IsNullOrEmpty(JsonpCallback);
            }
        }

        private string JsonpCallback
        {
            get
            {
                return Context.Request.Query["callback"];
            }
        }

        public override bool SupportsKeepAlive
        {
            get
            {
                return !IsJsonp;
            }
        }

        public override bool RequiresTimeout
        {
            get
            {
                return true;
            }
        }

        // This should be ok to do since long polling request never hang around too long
        // so we won't bloat memory
        protected override int MaxMessages
        {
            get
            {
                return 5000;
            }
        }

        protected override bool SuppressReconnect
        {
            get
            {
                return !Context.Request.LocalPath().EndsWith("/reconnect", StringComparison.OrdinalIgnoreCase);
            }
        }

        protected override async Task InitializeMessageId()
        {
            _lastMessageId = Context.Request.Query["messageId"];

            if (_lastMessageId == null)
            {
                var form = await Context.Request.GetFormAsync().PreserveCulture();
                _lastMessageId = form["messageId"];
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification = "This is for async.")]
        public override async Task<string> GetGroupsToken()
        {
            var groupsToken = Context.Request.Query["groupsToken"];

            if (groupsToken == null)
            {
                var form = await Context.Request.GetFormAsync().PreserveCulture();
                groupsToken = form["groupsToken"];
            }
            return groupsToken;
        }

        public override Task KeepAlive()
        {
            // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
            return EnqueueOperation(state => PerformKeepAlive(state), this);
        }

        public override Task Send(PersistentResponse response)
        {
            Heartbeat.MarkConnection(this);

            AddTransportData(response);

            // This overload is only used in response to /connect, /poll and /reconnect requests,
            // so the response will have already been initialized by ProcessMessages.
            var context = new LongPollingTransportContext(this, response);
            return EnqueueOperation(state => PerformPartialSend(state), context);
        }

        public override Task Send(object value)
        {
            var context = new LongPollingTransportContext(this, value);

            // This overload is only used in response to /send requests,
            // so the response will be uninitialized.
            return EnqueueOperation(state => PerformCompleteSend(state), context);
        }

        protected override Task<bool> OnMessageReceived(PersistentResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException("response");
            }

            response.Reconnect = HostShutdownToken.IsCancellationRequested;

            Task task = TaskAsyncHelper.Empty;

            if (response.Aborted)
            {
                // If this was a clean disconnect then raise the event
                task = Abort();
            }

            if (response.Terminal)
            {
                // If the response wasn't sent, send it before ending the request
                if (!_responseSent)
                {
                    // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
                    return task.Then((transport, resp) => transport.Send(resp), this, response)
                               .Then(() =>
                               {
                                   _transportLifetime.Complete();

                                   return TaskAsyncHelper.False;
                               });
                }

                // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
                return task.Then(() =>
                {
                    _transportLifetime.Complete();

                    return TaskAsyncHelper.False;
                });
            }

            // Mark the response as sent
            _responseSent = true;

            // Send the response and return false
            // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
            return task.Then((transport, resp) => transport.Send(resp), this, response)
                       .Then(() => TaskAsyncHelper.False);
        }

        protected internal override Task InitializeResponse(ITransportConnection connection)
        {
            // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
            return base.InitializeResponse(connection)
                       .Then(s => WriteInit(s), this);
        }

        protected override async Task ProcessSendRequest()
        {
            IReadableStringCollection form = await Context.Request.GetFormAsync().PreserveCulture();
            string data = form["data"] ?? Context.Request.Query["data"];

            if (Received != null)
            {
                await Received(data).PreserveCulture();
            }
        }

        private static Task WriteInit(LongPollingTransport transport)
        {
            transport.Context.Response.ContentType = transport.IsJsonp ? JsonUtility.JavaScriptMimeType : JsonUtility.JsonMimeType;
            return transport.Context.Response.Flush();
        }

        private static Task PerformKeepAlive(object state)
        {
            var transport = (LongPollingTransport)state;

            if (!transport.IsAlive)
            {
                return TaskAsyncHelper.Empty;
            }

            transport.Context.Response.Write(new ArraySegment<byte>(_keepAlive));

            return transport.Context.Response.Flush();
        }

        private static Task PerformPartialSend(object state)
        {
            var context = (LongPollingTransportContext)state;

            if (!context.Transport.IsAlive)
            {
                return TaskAsyncHelper.Empty;
            }

            using (var writer = new BinaryMemoryPoolTextWriter(context.Transport.Pool))
            {
                if (context.Transport.IsJsonp)
                {
                    writer.Write(context.Transport.JsonpCallback);
                    writer.Write("(");
                }

                context.Transport.JsonSerializer.Serialize(context.State, writer);

                if (context.Transport.IsJsonp)
                {
                    writer.Write(");");
                }

                writer.Flush();

                context.Transport.Context.Response.Write(writer.Buffer);
            }

            return context.Transport.Context.Response.Flush();
        }

        private static Task PerformCompleteSend(object state)
        {
            var context = (LongPollingTransportContext)state;

            if (!context.Transport.IsAlive)
            {
                return TaskAsyncHelper.Empty;
            }

            context.Transport.Context.Response.ContentType = context.Transport.IsJsonp ?
                JsonUtility.JavaScriptMimeType :
                JsonUtility.JsonMimeType;

            return PerformPartialSend(state);
        }

        private void AddTransportData(PersistentResponse response)
        {
            if (_pollDelay != TimeSpan.Zero)
            {
                response.LongPollDelay = (long)_pollDelay.TotalMilliseconds;
            }
        }

        private class LongPollingTransportContext
        {
            public object State;
            public LongPollingTransport Transport;

            public LongPollingTransportContext(LongPollingTransport transport, object state)
            {
                State = state;
                Transport = transport;
            }
        }
    }
}
