// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.


using System;
using System.Threading.Tasks;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.SignalR.Json;
using Microsoft.AspNet.Builder;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.OptionsModel;

namespace Microsoft.AspNet.SignalR.Hosting
{
    public class PersistentConnectionMiddleware
    {
        private readonly Type _connectionType;
        private readonly IOptionsAccessor<SignalROptions> _optionsAccessor;
        private readonly RequestDelegate _next;
        private readonly IServiceProvider _serviceProvider;

        public PersistentConnectionMiddleware(RequestDelegate next,
                                              Type connectionType,
                                              IOptionsAccessor<SignalROptions> optionsAccessor,
                                              IServiceProvider serviceProvider)
        {
            _next = next;
            _serviceProvider = serviceProvider;
            _connectionType = connectionType;
            _optionsAccessor = optionsAccessor;
        }

        public Task Invoke(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            if (JsonUtility.TryRejectJSONPRequest(_optionsAccessor.Options, context))
            {
                return TaskAsyncHelper.Empty;
            }

            var connection = ActivatorUtilities.CreateInstance(_serviceProvider, _connectionType) as PersistentConnection;

            connection.Initialize(_serviceProvider);

            return connection.ProcessRequest(context);
        }
    }
}
