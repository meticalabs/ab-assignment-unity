using System;
using System.Collections.Generic;

namespace Metica.Assignment {
    /// <summary>
    /// One event waiting for Metica ingestion. The id, time and user are fixed when the event is logged, so a retry
    /// sends the same event.
    /// </summary>
    internal sealed class QueuedEvent {
        public string Name;
        /// <summary>Lowercase UUID v4.</summary>
        public string EventId;
        /// <summary>Unix epoch milliseconds when the event was logged.</summary>
        public long EventTimeMs;
        public string UserId;
        /// <summary>Flat; consent_status is added at send time.</summary>
        public Dictionary<string, object> Payload;
    }

    /// <summary>
    /// Persisted, bounded FIFO of events waiting for Metica ingestion. An event leaves it only when ingestion accepted it
    /// or rejected it for good. Main thread only.
    /// </summary>
    internal sealed class EventQueue {
        public const int Capacity = 100;
        internal const string StoreKey = "metica_assignment_queue_v1";

        private const string NameField = "name";
        private const string PayloadField = "payload";
        // Absent in queues stored by 0.1.0; such events get them on load.
        private const string IdField = "id";
        private const string TimeField = "time";
        private const string UserField = "user";

        private readonly IKeyValueStore _store;
        private readonly List<QueuedEvent> _events = new List<QueuedEvent>();

        public EventQueue(IKeyValueStore store) {
            _store = store;
        }

        public int Count => _events.Count;

        /// <summary>
        /// Loads events left over from earlier sessions. <paramref name="completeOldEvent"/> fills in the id, time and
        /// user of an event stored by 0.1.0, which kept only the name and payload. The device fields stored by 1.0.0
        /// ("env") are ignored, so those events are sent without them.
        /// </summary>
        public void Load(Action<QueuedEvent> completeOldEvent) {
            _events.Clear();
            string stored = _store.GetString(StoreKey);
            if (string.IsNullOrEmpty(stored)) {
                return;
            }
            if (!Json.TryParse(stored, out object root, out string error) || !(root is List<object> items)) {
                AssignmentLog.Warning("Stored event queue is unreadable and was cleared: " + error);
                _store.DeleteKey(StoreKey);
                return;
            }
            bool completed = false;
            foreach (object item in items) {
                if (!(item is Dictionary<string, object> entry) ||
                    !entry.TryGetValue(NameField, out object name) || !(name is string eventName) ||
                    !entry.TryGetValue(PayloadField, out object payload) || !(payload is Dictionary<string, object> fields)) {
                    AssignmentLog.Warning("Dropped an unreadable stored event.");
                    continue;
                }
                var e = new QueuedEvent { Name = eventName, Payload = ToValues(fields) };
                if (entry.TryGetValue(IdField, out object id) && id is string eventId &&
                    entry.TryGetValue(TimeField, out object time) && time is JsonNumber eventTime &&
                    eventTime.ToLongOrDouble() is long eventTimeMs &&
                    entry.TryGetValue(UserField, out object user) && user is string userId) {
                    e.EventId = eventId;
                    e.EventTimeMs = eventTimeMs;
                    e.UserId = userId;
                } else {
                    completeOldEvent(e);
                    completed = true;
                }
                _events.Add(e);
            }
            if (completed) {
                Persist();
            }
        }

        public void Enqueue(QueuedEvent e) {
            // Round-trip through JSON so values have the same types whether the event is sent now or after a restart.
            e.Payload = RoundTrip(e.Payload);
            _events.Add(e);
            while (_events.Count > Capacity) {
                AssignmentLog.Warning("Event queue is full; dropped the oldest event " + _events[0].Name + ".");
                _events.RemoveAt(0);
            }
            Persist();
        }

        /// <summary>
        /// Up to <paramref name="max"/> events from the front of the queue, in order, with the same user id (the user id
        /// is also a query parameter of the request).
        /// </summary>
        public List<QueuedEvent> PeekBatch(int max) {
            var batch = new List<QueuedEvent>();
            foreach (QueuedEvent e in _events) {
                if (batch.Count == max || (batch.Count > 0 && e.UserId != batch[0].UserId)) {
                    break;
                }
                batch.Add(e);
            }
            return batch;
        }

        /// <summary>Removes the events with these ids; ids no longer queued are ignored.</summary>
        public void Remove(ICollection<string> eventIds) {
            int removed = _events.RemoveAll(e => eventIds.Contains(e.EventId));
            if (removed > 0) {
                Persist();
            }
        }

        private void Persist() {
            var items = new List<object>(_events.Count);
            foreach (QueuedEvent e in _events) {
                items.Add(new Dictionary<string, object> {
                    { NameField, e.Name },
                    { PayloadField, e.Payload },
                    { IdField, e.EventId },
                    { TimeField, e.EventTimeMs },
                    { UserField, e.UserId }
                });
            }
            _store.SetString(StoreKey, Json.Write(items));
            _store.Save();
        }

        private static Dictionary<string, object> RoundTrip(Dictionary<string, object> values) {
            Json.TryParse(Json.Write(values), out object copy, out _);
            return ToValues((Dictionary<string, object>)copy);
        }

        private static Dictionary<string, object> ToValues(Dictionary<string, object> parsed) {
            var values = new Dictionary<string, object>(parsed.Count);
            foreach (KeyValuePair<string, object> pair in parsed) {
                if (pair.Value is JsonNumber number) {
                    values[pair.Key] = number.ToLongOrDouble();
                } else {
                    values[pair.Key] = pair.Value;
                }
            }
            return values;
        }
    }
}
