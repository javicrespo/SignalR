﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SignalR.Client.Transports
{
    public abstract class HttpBasedTransport : IClientTransport
    {
        // The outstanding http request
        private HttpWebRequest _httpRequest;
        private readonly object _lockObj = new object();

        // The receive query string
        private const string _receiveQueryString = "?transport={0}&connectionId={1}&messageId={2}&groups={3}&connectionData={4}";

        // The send query string
        private const string _sendQueryString = "?transport={0}&connectionId={1}";

        // The transport name
        protected readonly string _transport;

        public HttpBasedTransport(string transport)
        {
            _transport = transport;
        }

        protected HttpWebRequest ActiveRequest
        {
            get
            {
                return _httpRequest;
            }
            set
            {
                lock (_lockObj)
                {
                    _httpRequest = value;
                }
            }
        }

        public Task Start(Connection connection, string data)
        {
            var tcs = new TaskCompletionSource<object>();

            OnStart(connection, data, () => tcs.SetResult(null), exception => tcs.SetException(exception));

            return tcs.Task;
        }

        protected abstract void OnStart(Connection connection, string data, Action initializeCallback, Action<Exception> errorCallback);

        public Task<T> Send<T>(Connection connection, string data)
        {
            string url = connection.Url + "send";

            url += String.Format(_sendQueryString, _transport, connection.ConnectionId);

            var postData = new Dictionary<string, string> {
                { "data", data }
            };

            return HttpHelper.PostAsync(url, connection.PrepareRequest, postData).Then(response =>
            {
                string raw = response.ReadAsString();

                if (String.IsNullOrEmpty(raw))
                {
                    return default(T);
                }

                return JsonConvert.DeserializeObject<T>(raw);
            });
        }

        protected string GetReceiveQueryString(Connection connection, string data)
        {
            return String.Format(_receiveQueryString,
                                 _transport,
                                 Uri.EscapeDataString(connection.ConnectionId),
                                 Convert.ToString(connection.MessageId),
                                 Uri.EscapeDataString(String.Join(",", connection.Groups.ToArray())),
                                 data);
        }

        protected virtual Action<HttpWebRequest> PrepareRequest(Connection connection)
        {
            return request =>
            {
                // Setup the user agent along with any other defaults
                connection.PrepareRequest(request);

                ActiveRequest = request;
            };
        }

        protected static bool IsRequestAborted(Exception exception)
        {
            var webException = exception as WebException;
            return (webException != null && webException.Status == WebExceptionStatus.RequestCanceled);
        }

        public void Stop(Connection connection)
        {
            if (_httpRequest != null)
            {
                lock (_lockObj)
                {
                    if (_httpRequest != null)
                    {
                        try
                        {
                            _httpRequest.Abort();
                            _httpRequest = null;
                        }
                        catch (NotImplementedException)
                        {
                            // If this isn't implemented then do nothing
                        }
                    }
                }
            }
        }

        protected static void OnMessage(Connection connection, string response)
        {
            if (connection.MessageId == null)
            {
                connection.MessageId = 0;
            }

            try
            {
                var result = JValue.Parse(response);

                if (!result.HasValues)
                {
                    return;
                }

                var messages = result["Messages"] as JArray;

                if (messages != null)
                {
                    foreach (var message in messages)
                    {
                        try
                        {
                            connection.OnReceived(message.ToString());
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Failed to process message: {0}", ex);
                            connection.OnError(ex);
                        }
                    }

                    connection.MessageId = result["MessageId"].Value<long>();

                    var transportData = result["TransportData"] as JObject;

                    if (transportData != null)
                    {
                        var groups = (JArray)transportData["Groups"];
                        if (groups != null)
                        {
                            connection.Groups = groups.Select(token => token.Value<string>());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to response: {0}", ex);
                connection.OnError(ex);
            }
        }
    }
}