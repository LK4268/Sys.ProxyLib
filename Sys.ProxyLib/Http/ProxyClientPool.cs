﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Sys.ProxyLib.Helpers;
using Sys.ProxyLib.Proxy;

namespace Sys.ProxyLib.Http
{
    internal class ProxyClientPool : IDisposable
    {
        private uint _size;
        private int _isDisposed = 0;
        private readonly object _lock = new object();
        private RemoteCertificateValidationCallback _callback = null;
        private Func<CancellationToken, Task<IProxyClient>> _proxyClientFactory = null;
        private IDictionary<HostPort, Pool<ProxyClientWrapper>> _cachedPools = new Dictionary<HostPort, Pool<ProxyClientWrapper>>();

        public ProxyClientPool(uint size, Func<CancellationToken, Task<IProxyClient>> proxyClientFactory, RemoteCertificateValidationCallback callback = null)
        {
            if (proxyClientFactory == null)
            {
                throw new ArgumentNullException(nameof(proxyClientFactory));
            }

            _size = size;
            _callback = callback;
            _proxyClientFactory = proxyClientFactory;
        }

        public Task<PooledObject<ProxyClientWrapper>> GetClientAsync(Uri uri, TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            Pool<ProxyClientWrapper> pool = null;

            HostPort key = new HostPort() { Host = uri.Host, Port = uri.Port, IsSsl = uri.IsHttps() };

            if (!_cachedPools.TryGetValue(key, out pool))
            {
                lock (_lock)
                {
                    if (!_cachedPools.TryGetValue(key, out pool))
                    {
                        pool = new Pool<ProxyClientWrapper>(
                            _size,
                            async cancel => new ProxyClientWrapper(await _proxyClientFactory.Invoke(cancel), key, _callback),
                            whenDropAndNew: client => client.IsBroken);

                        _cachedPools.Add(key, pool);
                    }
                }
            }

            return pool.GetObjectAsync(timeout, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
            {
                foreach (var pool in _cachedPools.Values)
                {
                    pool.Dispose();
                }

                _cachedPools.Clear();
            }
        }
    }

    internal struct HostPort
    {
        public string Host { get; set; }

        public int Port { get; set; }

        public bool IsSsl { get; set; }
    }

    internal class ProxyClientWrapper : IDisposable
    {
        private HostPort _key;
        private int _isDisposed = 0;
        private IProxyClient _proxyClient = null;
        private RemoteCertificateValidationCallback _callback = null;
        private Stream _stream = null;

        internal ProxyClientWrapper(IProxyClient proxyClient, HostPort key, RemoteCertificateValidationCallback callback = null)
        {
            _key = key;
            _proxyClient = proxyClient;
            _callback = callback;
        }

        public bool IsBroken => _stream != null && !_proxyClient.Connected;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
            {
                _proxyClient?.Dispose();
                _stream?.Dispose();
            }
        }

        public async Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_stream == null)
            {
                _stream = await Get_StreamAsync().ConfigureAwait(false);
            }

            return _stream;

            async Task<Stream> Get_StreamAsync()
            {
                await _proxyClient.ConnectionAsync(_key.Host, _key.Port, cancellationToken).ConfigureAwait(false);

                Stream stream = _proxyClient.GetStream();

                if (_key.IsSsl)
                {
                    var sslStream = new SslStream(stream, false, _callback);
                    await sslStream.AuthenticateAsClientAsync(_key.Host).ConfigureAwait(false);
                    stream = sslStream;
                }

                return stream;
            }
        }
    }
}