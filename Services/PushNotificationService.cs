using System.Text.Json;
using WebPush;

namespace Weaver.Services;

public class PushNotificationService
{
    private readonly DatabaseService _db;
    private VapidDetails? _vapid;
    private PushSubscription? _subscription;
    private static readonly object _lock = new();

    public PushNotificationService(DatabaseService db)
    {
        _db = db;
        EnsureVapidKeys();
    }

    public string GetVapidPublicKey()
    {
        lock (_lock) return _vapid?.PublicKey ?? "";
    }

    public void SetSubscription(PushSubscription sub)
    {
        lock (_lock) _subscription = sub;
    }

    public async Task SendNotificationAsync(string title, string body)
    {
        PushSubscription? sub;
        VapidDetails? vapid;
        lock (_lock) { sub = _subscription; vapid = _vapid; }
        if (sub == null || vapid == null) return;
        try
        {
            var client = new WebPushClient();
            var payload = JsonSerializer.Serialize(new { title, body, icon = "/weavericon.png" });
            await client.SendNotificationAsync(sub, payload, vapid);
        }
        catch { }
    }

    private void EnsureVapidKeys()
    {
        try
        {
            var json = _db.GetVapidKeys();
            if (!string.IsNullOrEmpty(json))
            {
                var keys = JsonSerializer.Deserialize<VapidKeyStore>(json);
                if (keys != null && !string.IsNullOrEmpty(keys.PublicKey) && !string.IsNullOrEmpty(keys.PrivateKey))
                {
                    _vapid = new VapidDetails("mailto:weaver@localhost", keys.PublicKey, keys.PrivateKey);
                    return;
                }
            }
            var newKeys = VapidHelper.GenerateVapidKeys();
            var store = new VapidKeyStore { PublicKey = newKeys.PublicKey, PrivateKey = newKeys.PrivateKey };
            _db.SetVapidKeys(JsonSerializer.Serialize(store));
            _vapid = newKeys;
        }
        catch
        {
            _vapid = VapidHelper.GenerateVapidKeys();
        }
    }

    private class VapidKeyStore
    {
        public string PublicKey { get; set; } = "";
        public string PrivateKey { get; set; } = "";
    }
}
