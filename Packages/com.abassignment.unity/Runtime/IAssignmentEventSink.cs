using System;
using System.Collections.Generic;

namespace AB.Assignment {
    /// <summary>
    /// Obsolete; kept so 0.1.0 integrations still compile. Events always go to Metica through the library's built-in
    /// sender (see <see cref="ABAssignment.Initialize"/>), and <see cref="ABAssignment.SetEventSink"/> ignores
    /// the sink. Subscribe to <see cref="ABAssignment.EventLogged"/> for copies of the events.
    /// </summary>
    [Obsolete("Ignored. Subscribe to ABAssignment.EventLogged to get copies of the events.")]
    public interface IAssignmentEventSink {
        bool IsReady { get; }

        void Send(string eventName, IReadOnlyDictionary<string, object> payload);
    }
}
