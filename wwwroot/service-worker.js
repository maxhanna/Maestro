self.addEventListener('install', function () {
    self.skipWaiting();
});

self.addEventListener('activate', function () {
    self.clients.claim();
});

self.addEventListener('push', function (event) {
    var data = {};
    try { data = event.data ? event.data.json() : {}; } catch (e) {}
    var title = data.title || 'Weaver';
    var body = data.body || '';
    var icon = data.icon || '/weavericon.png';
    event.waitUntil(
        self.registration.showNotification(title, {
            body: body,
            icon: icon,
            silent: false,
            requireInteraction: true,
            tag: 'weaver-' + Date.now()
        })
    );
});

self.addEventListener('message', function (event) {
    if (event.data && event.data.type === 'show-notification') {
        self.registration.showNotification(
            event.data.title || 'Weaver',
            {
                body: event.data.body || '',
                icon: event.data.icon || '/weavericon.png',
                silent: false,
                requireInteraction: true,
                tag: 'weaver-' + Date.now(),
                data: event.data.data || {}
            }
        );
    }
});

self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    var url = event.notification.data && event.notification.data.url ? event.notification.data.url : '/';
    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clientList) {
            for (var i = 0; i < clientList.length; i++) {
                var client = clientList[i];
                if (client.url.indexOf(self.location.origin) === 0 && 'focus' in client) {
                    return client.focus();
                }
            }
            if (clients.openWindow) return clients.openWindow(url);
        })
    );
});
