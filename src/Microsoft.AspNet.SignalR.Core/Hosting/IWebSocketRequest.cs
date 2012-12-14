﻿using System;
using System.Threading.Tasks;

namespace Microsoft.AspNet.SignalR
{
    public interface IWebSocketRequest : IRequest
    {
        /// <summary>
        /// Accepts an websocket request using the specified user function.
        /// </summary>
        /// <param name="callback">The callback that fires when the websocket is ready.</param>
        Task AcceptWebSocketRequest(Func<IWebSocket, Task> callback);
    }
}