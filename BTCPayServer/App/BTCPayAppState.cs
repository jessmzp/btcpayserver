#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.App.BackupStorage;
using BTCPayServer.Client.App;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Notifications;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Protocol;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NewBlockEvent = BTCPayServer.Events.NewBlockEvent;

namespace BTCPayServer.Controllers;

public record ConnectedInstance(
    string UserId,
    long? DeviceIdentifier,
    bool Master,
    //TODOO: use an access key generated by the app, this allows us to revoke access to the app or even have permissions on what the server can do 
    // string ProvidedAccessKey,
    HashSet<string> Groups);

public class BTCPayAppState : IHostedService
{
    private readonly IMemoryCache _memoryCache;
    private readonly ApplicationDbContextFactory _dbContextFactory;
    private readonly StoreRepository _storeRepository;
    private readonly IHubContext<BTCPayAppHub, IBTCPayAppHubClient> _hubContext;
    private readonly ILogger<BTCPayAppState> _logger;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly IServiceProvider _serviceProvider;
    private CompositeDisposable? _compositeDisposable;
    private ExplorerClient? ExplorerClient { get; set; }
    private DerivationSchemeParser? _derivationSchemeParser;


    public ConcurrentDictionary<string, ConnectedInstance> Connections { get; set; } = new();

    private async Task<long?> GetGracefulDisconnectDeviceIdentifier(string userId)
    {
        var dict = await _memoryCache.GetOrCreateAsync("app-graceful-disconnects", async entry =>
        {
            await using var ctx = _dbContextFactory.CreateContext();
            return (await ctx.AppStorageItems.AsNoTracking().Where(data =>
                    data.Key == "masterDevice").ToListAsync())
                .ToDictionary(data => data.UserId, data => long.Parse(data.Value));
        });
        if (dict?.TryGetValue(userId, out var deviceIdentifier) is true)
        {
            return deviceIdentifier;
        }

        return null;
    }

    private async Task RemoveGracefulDisconnectDeviceIdentifier(string userId)
    {
        if (await GetGracefulDisconnectDeviceIdentifier(userId) is null)
        {
            return;
        }

        await using var ctx = _dbContextFactory.CreateContext();
        var entity = await ctx.AppStorageItems.FirstOrDefaultAsync(data =>
            data.Key == "masterDevice" && data.UserId == userId);
        if (entity is not null)
        {
            ctx.AppStorageItems.Remove(entity);
            await ctx.SaveChangesAsync();
            _memoryCache.Remove("app-graceful-disconnects");
        }
    }

    private async Task AddGracefulDisconnectDeviceIdentifier(string userId, long deviceIdentifier)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var entity = await ctx.AppStorageItems.FirstOrDefaultAsync(data =>
            data.Key == "masterDevice" && data.UserId == userId);
        if (entity is null)
        {
            entity = new AppStorageItemData()
            {
                Key = "masterDevice", UserId = userId, Value = Encoding.UTF8.GetBytes(deviceIdentifier.ToString()),
            };
            await ctx.AppStorageItems.AddAsync(entity);
            await ctx.SaveChangesAsync();
            _memoryCache.Remove("app-graceful-disconnects");
        }
    }

    private static readonly SemaphoreSlim _lock = new(1, 1);

    private CancellationTokenSource? _cts;

    public event EventHandler<(string, LightningInvoice)>? OnInvoiceUpdate;

    public BTCPayAppState(
        IMemoryCache memoryCache,
        ApplicationDbContextFactory dbContextFactory,
        StoreRepository storeRepository,
        IHubContext<BTCPayAppHub, IBTCPayAppHubClient> hubContext,
        ILogger<BTCPayAppState> logger,
        ExplorerClientProvider explorerClientProvider,
        BTCPayNetworkProvider networkProvider,
        EventAggregator eventAggregator, IServiceProvider serviceProvider)
    {
        _memoryCache = memoryCache;
        _dbContextFactory = dbContextFactory;
        _storeRepository = storeRepository;
        _hubContext = hubContext;
        _logger = logger;
        _explorerClientProvider = explorerClientProvider;
        _networkProvider = networkProvider;
        _eventAggregator = eventAggregator;
        _serviceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts ??= new CancellationTokenSource();
        ExplorerClient = _explorerClientProvider.GetExplorerClient("BTC");
        _derivationSchemeParser = new DerivationSchemeParser(_networkProvider.BTC);
        _compositeDisposable = new CompositeDisposable();
        _compositeDisposable.Add(_eventAggregator.Subscribe<NewBlockEvent>(OnNewBlock));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<NewOnChainTransactionEvent>(OnNewTransaction));
        _compositeDisposable.Add(
            _eventAggregator.SubscribeAsync<UserNotificationsUpdatedEvent>(UserNotificationsUpdatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<InvoiceEvent>(InvoiceChangedEvent));
        // User events
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<UserEvent.Updated>(UserUpdatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<UserEvent.Deleted>(UserDeletedEvent));
        // Store events
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<StoreEvent.Created>(StoreCreatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<StoreEvent.Updated>(StoreUpdatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<StoreEvent.Removed>(StoreRemovedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<StoreRoleEvent.Added>(StoreRoleAddedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<StoreRoleEvent.Updated>(StoreRoleUpdatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<StoreRoleEvent.Removed>(StoreRoleRemovedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<UserStoreEvent.Added>(StoreUserAddedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<UserStoreEvent.Updated>(StoreUserUpdatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<UserStoreEvent.Removed>(StoreUserRemovedEvent));
        // App events
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<AppEvent.Created>(AppCreatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<AppEvent.Updated>(AppUpdatedEvent));
        _compositeDisposable.Add(_eventAggregator.SubscribeAsync<AppEvent.Deleted>(AppDeletedEvent));
        _ = UpdateNodeInfo();
        return Task.CompletedTask;
    }

    private async Task UserUpdatedEvent(UserEvent.Updated arg)
    {
        var ev = new ServerEvent { Type = "user-updated", UserId = arg.User.Id };
        await _hubContext.Clients.Group(arg.User.Id).NotifyServerEvent(ev);
    }

    private async Task UserDeletedEvent(UserEvent.Deleted arg)
    {
        var ev = new ServerEvent { Type = "user-deleted", UserId = arg.User.Id };
        await _hubContext.Clients.Group(arg.User.Id).NotifyServerEvent(ev);
    }

    private async Task InvoiceChangedEvent(InvoiceEvent arg)
    {
        var ev = new ServerEvent
        {
            Type = "invoice-updated",
            StoreId = arg.Invoice.StoreId,
            InvoiceId = arg.InvoiceId,
            Detail = arg.Invoice.Status.ToString()
        };
        await _hubContext.Clients.Group(arg.Invoice.StoreId).NotifyServerEvent(ev);
    }

    private async Task UserNotificationsUpdatedEvent(UserNotificationsUpdatedEvent arg)
    {
        var ev = new ServerEvent { Type = "notifications-updated", UserId = arg.UserId };
        await _hubContext.Clients.Group(arg.UserId).NotifyServerEvent(ev);
    }

    private async Task StoreCreatedEvent(StoreEvent.Created arg)
    {
        var ev = new ServerEvent { Type = "store-created", StoreId = arg.StoreId };

        if (arg.StoreUsers?.Any() is true)
        {
            foreach (var su in arg.StoreUsers)
            {
                var cIds = Connections.Where(pair => pair.Value.UserId == su.UserId).Select(pair => pair.Key).ToArray();
                await AddToGroup(arg.StoreId, cIds);
            }
        }

        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task StoreUpdatedEvent(StoreEvent.Updated arg)
    {
        var ev = new ServerEvent { Type ="store-updated", StoreId = arg.StoreId, Detail = arg.Detail };
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task StoreRemovedEvent(StoreEvent.Removed arg)
    {
        var ev = new ServerEvent { Type = "store-removed", StoreId = arg.StoreId};
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task StoreUserAddedEvent(UserStoreEvent.Added arg)
    {
        var cIds = Connections.Where(pair => pair.Value.UserId == arg.UserId).Select(pair => pair.Key).ToArray();
        await AddToGroup(arg.StoreId, cIds);
        var ev = new ServerEvent { Type = "user-store-added", StoreId = arg.StoreId, UserId = arg.UserId };
        await _hubContext.Clients.Groups(arg.StoreId, arg.UserId).NotifyServerEvent(ev);
    }

    private async Task StoreUserUpdatedEvent(UserStoreEvent.Updated arg)
    {
        var ev = new ServerEvent { Type = "user-store-updated", StoreId = arg.StoreId, UserId = arg.UserId };
        await _hubContext.Clients.Groups(arg.StoreId, arg.UserId).NotifyServerEvent(ev);
    }

    private async Task StoreUserRemovedEvent(UserStoreEvent.Removed arg)
    {
        var ev = new ServerEvent { Type = "user-store-removed", StoreId = arg.StoreId, UserId = arg.UserId };
        await _hubContext.Clients.Groups(arg.StoreId, arg.UserId).NotifyServerEvent(ev);

        await RemoveFromGroup(arg.StoreId,
            Connections.Where(pair => pair.Value.UserId == arg.UserId).Select(pair => pair.Key).ToArray());
    }

    private async Task StoreRoleAddedEvent(StoreRoleEvent.Added arg)
    {
        var ev = new ServerEvent { Type = "store-role-added", StoreId = arg.StoreId };
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task StoreRoleUpdatedEvent(StoreRoleEvent.Updated arg)
    {
        var ev = new ServerEvent { Type = "store-role-updated", StoreId = arg.StoreId };
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task StoreRoleRemovedEvent(StoreRoleEvent.Removed arg)
    {
        var ev = new ServerEvent { Type = "store-role-removed", StoreId = arg.StoreId };
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task AppCreatedEvent(AppEvent.Created arg)
    {
        var ev = new ServerEvent { Type = "app-created", StoreId = arg.StoreId, AppId = arg.AppId, Detail = arg.Detail };
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task AppUpdatedEvent(AppEvent.Updated arg)
    {
        var ev = new ServerEvent { Type = "app-updated", StoreId = arg.StoreId, AppId = arg.AppId, Detail = arg.Detail };
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private async Task AppDeletedEvent(AppEvent.Deleted arg)
    {
        var ev = new ServerEvent { Type = "app-deleted", StoreId = arg.StoreId, AppId = arg.AppId, Detail = arg.Detail };
        await _hubContext.Clients.Group(arg.StoreId).NotifyServerEvent(ev);
    }

    private string _nodeInfo = string.Empty;

    private async Task UpdateNodeInfo()
    {
        var lastError = "";
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var res = await _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>()
                    .GetLightningHandler(ExplorerClient.CryptoCode).GetNodeInfo(
                        new LightningPaymentMethodConfig()
                        {
                            InternalNodeRef = LightningPaymentMethodConfig.InternalNode
                        },
                        null,
                        false, false);
                if (res.Any())
                {
                    var newInf = res.First();
                    if (_networkProvider.NetworkType == ChainName.Regtest)
                    {
                        newInf = new NodeInfo(newInf.NodeId, "127.0.0.1", 30893);
                    }

                    if (newInf.ToString() != _nodeInfo)
                    {
                        _nodeInfo = newInf.ToString();
                        await _hubContext.Clients.All.NotifyServerNode(_nodeInfo);
                    }
                }
            }
            catch (Exception e)
            {
                if (lastError != e.Message)
                {
                    lastError = e.Message;
                    _logger.LogError(e, "Error during node info update");
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(string.IsNullOrEmpty(_nodeInfo) ? 1 : 5), _cts.Token);
        }
    }

    private async Task OnNewTransaction(NewOnChainTransactionEvent obj)
    {
        if (!obj.PaymentMethodId.ToString().StartsWith("BTC")) return;
        var identifier = obj.NewTransactionEvent.TrackedSource.ToString()!;
        var explorer = _explorerClientProvider.GetExplorerClient("BTC");
        var expandedTx = await explorer.GetTransactionAsync(obj.NewTransactionEvent.TrackedSource,
            obj.NewTransactionEvent.TransactionData.TransactionHash);
        await _hubContext.Clients
            .Group(identifier)
            .TransactionDetected(new TransactionDetectedRequest
            {
                SpentScripts = expandedTx.Inputs.Select(input => input.ScriptPubKey.ToHex()).ToArray(),
                ReceivedScripts = expandedTx.Outputs.Select(output => output.ScriptPubKey.ToHex()).ToArray(),
                TxId = obj.NewTransactionEvent.TransactionData.TransactionHash.ToString(),
                Confirmed = obj.NewTransactionEvent.BlockId is not null &&
                            obj.NewTransactionEvent.BlockId != uint256.Zero,
                Identifier = identifier
            });
    }

    private void OnNewBlock(NewBlockEvent obj)
    {
        if (obj.CryptoCode != "BTC") return;
        _hubContext.Clients.All.NewBlock(obj.Hash.ToString());
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _compositeDisposable?.Dispose();
        return Task.CompletedTask;
    }
    
    private async Task<bool> IsTracked(TrackedSource trackedSource)
    {
       
      
        return true;
    }

    public async Task<AppHandshakeResponse> Handshake(string contextConnectionId, AppHandshake handshake)
    {
        var ack = new List<string>();
        foreach (var ts in handshake.Identifiers)
        {
            try
            {
                if (TrackedSource.TryParse(ts, out var trackedSource, ExplorerClient.Network) && await IsTracked(trackedSource))
                {
                    ack.Add(ts);
                    await AddToGroup(ts, contextConnectionId);
                }

            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error during handshake");
                throw;
            }
        }

        return new AppHandshakeResponse() {IdentifiersAcknowledged = ack.ToArray()};
    }

    public async Task<Dictionary<string, string>> Pair(string contextConnectionId, PairRequest request)
    {
        var result = new Dictionary<string, string>();
        foreach (var derivation in request.Derivations)
        {
            if (derivation.Value is null)
            {
                var id = await ExplorerClient.CreateGroupAsync();

                result.Add(derivation.Key, id.TrackedSource);
            }
            else
            {
                var strategy = _derivationSchemeParser.ParseOutputDescriptor(derivation.Value);
                result.Add(derivation.Key, TrackedSource.Create(strategy.Item1).ToString());
            }
        }

        await Handshake(contextConnectionId, new AppHandshake {Identifiers = result.Values.ToArray()});
        return result;
    }

    public async Task<bool> DeviceMasterSignal(string contextConnectionId, long deviceIdentifier, bool active)
    {
        var updated = false;
        var result = false;
        await _lock.WaitAsync();
        ConnectedInstance? connectedInstance = null;
        try
        {
            if (!Connections.TryGetValue(contextConnectionId, out connectedInstance))
            {
                _logger.LogWarning("DeviceMasterSignal called on non existing connection");
                result = false;
                return result;
            }
            else if (connectedInstance.DeviceIdentifier != null &&
                     connectedInstance.DeviceIdentifier != deviceIdentifier)
            {
                _logger.LogWarning("DeviceMasterSignal called with different device identifier");
                result = false;
                return result;
            }

            if (connectedInstance.DeviceIdentifier == null)
            {
                _logger.LogInformation("DeviceMasterSignal called with device identifier {deviceIdentifier}",
                    deviceIdentifier);
                connectedInstance = connectedInstance with {DeviceIdentifier = deviceIdentifier};
                Connections[contextConnectionId] = connectedInstance;
                
            }

            if (connectedInstance.Master == active)
            {
                _logger.LogInformation("DeviceMasterSignal called with same active state");
                result = true;
                
                return result;
            }
            else if (active)
            {
                //check if there is any other master connection with the same user id
                if (Connections.Values.Any(c => c.UserId == connectedInstance.UserId && c.Master))
                {
                    _logger.LogWarning(
                        "DeviceMasterSignal called with active state but there is already a master connection");
                    result = false;
                    return result;
                }
                else if (await GetGracefulDisconnectDeviceIdentifier(connectedInstance.UserId) is { } dI &&
                         dI != deviceIdentifier)
                {
                    _logger.LogWarning(
                        "DeviceMasterSignal called with active state but the master connection was ungracefully disconnected");

                    connectedInstance = connectedInstance with {Master = false};
                    Connections[contextConnectionId] = connectedInstance;
                    result = false;
                    return result;
                }
                else
                {
                    _logger.LogInformation("DeviceMasterSignal called with active state");
                    connectedInstance = connectedInstance with {Master = true};
                    Connections[contextConnectionId] = connectedInstance;
                    await RemoveGracefulDisconnectDeviceIdentifier(connectedInstance.UserId);
                    result = true;
                    updated = true;
                    return result;
                }
            }
            else
            {
                _logger.LogInformation("DeviceMasterSignal called with inactive state");
                connectedInstance = connectedInstance with {Master = false};
                Connections[contextConnectionId] = connectedInstance;

                MasterUserDisconnected?.Invoke(this, connectedInstance.UserId);
                result = true;
                updated = true;
                return result;
            }
        }
        finally
        {
            _lock.Release();
            if (result && connectedInstance is not null && updated)
            {
                var connIds = Connections.Where(pair => pair.Value.UserId == connectedInstance.UserId)
                    .Select(pair => pair.Key)
                    .ToList();

                await _hubContext.Clients.Clients(connIds).MasterUpdated(active ? deviceIdentifier : null);
            }
        }
    }

    public async Task Disconnected(string contextConnectionId)
    {
        if (Connections.TryRemove(contextConnectionId, out var connectedInstance) && connectedInstance.Master)
        {
            MasterUserDisconnected?.Invoke(this, connectedInstance.UserId);
            await AddGracefulDisconnectDeviceIdentifier(connectedInstance.UserId,
                connectedInstance.DeviceIdentifier!.Value);
        }
    }

    public event EventHandler<string>? MasterUserDisconnected;

    public async Task Connected(string contextConnectionId, string userId)
    {
        Connections.TryAdd(contextConnectionId, new ConnectedInstance(userId, null, false, new HashSet<string>()));

        if (_nodeInfo.Length > 0)
            await _hubContext.Clients.Client(contextConnectionId).NotifyServerNode(_nodeInfo);

        await _hubContext.Clients.Client(contextConnectionId)
            .NotifyNetwork(_networkProvider.BTC.NBitcoinNetwork.ToString());

        var groups = (await _storeRepository.GetStoresByUserId(userId)).Select(store => store.Id).ToArray()
            .Concat(new[] {userId});

        foreach (var group in groups)
        {
            await AddToGroup(group, contextConnectionId);
        }
    }

    public async Task InvoiceUpdate(string contextConnectionId, LightningInvoice lightningInvoice)
    {
        if (!Connections.TryGetValue(contextConnectionId, out var connectedInstance) || !connectedInstance.Master)
        {
            return;
        }

        OnInvoiceUpdate?.Invoke(this, (connectedInstance.UserId, lightningInvoice));
    }

    //what are we adding to groups?
    //user id
    //store id(s)
    //tracked sources

    private async Task AddToGroup(string group, params string[] connectionIds)
    {
        foreach (var connectionId in connectionIds)
        {
            await _hubContext.Groups.AddToGroupAsync(connectionId, group);
            if (Connections.TryGetValue(connectionId, out var connectedInstance))
            {
                connectedInstance.Groups.Add(group);
            }
        }
    }

    private async Task RemoveFromGroup(string group, params string[] connectionIds)
    {
        foreach (var connectionId in connectionIds)
        {
            await _hubContext.Groups.RemoveFromGroupAsync(connectionId, group);
            if (Connections.TryGetValue(connectionId, out var connectedInstance))
            {
                connectedInstance.Groups.Remove(group);
            }
        }
    }

    public async Task<long?> GetCurrentMaster(string contextConnectionId)
    {
        if (Connections.TryGetValue(contextConnectionId, out var connectedInstance) && connectedInstance.Master)
        {
            return connectedInstance.DeviceIdentifier;
        }

        return null;
    }

    public async Task<bool> IsMaster(string userId, long deviceIdentifier)
    {
        return Connections.Values.Any(c => c.Master && c.DeviceIdentifier == deviceIdentifier && c.UserId == userId) ||
               await GetGracefulDisconnectDeviceIdentifier(userId) is { } dI && dI == deviceIdentifier;
    }

    public async Task GracefulDisconnect(string userId)
    {
        await RemoveGracefulDisconnectDeviceIdentifier(userId);
    }
}
