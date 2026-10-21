using System;
using System.Collections.Generic;

namespace Metica.Assignment {
    /// <summary>
    /// Obsolete; kept so 0.1.0 integrations still compile. Events always go to Metica through the library's built-in
    /// sender (see <see cref="MeticaAssignment.Initialize"/>), and <see cref="MeticaAssignment.SetEventSink"/> ignores
    /// the sink. Subscribe to <see cref="MeticaAssignment.EventLogged"/> for copies of the events.
    /// </summary>
    [Obsolete("Ignored. Subscribe to MeticaAssignment.EventLogged to get copies of the events.")]
    public interface IAssignmentEventSink {
        bool IsReady { get; }

        void Send(string eventName, IReadOnlyDictionary<string, object> payload);
    }
}
