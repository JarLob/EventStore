using System;
using EventStore.Core.Data;

namespace EventStore.Core.Services.PersistentSubscription {
	public struct OutstandingMessage {
		public readonly ResolvedEvent ResolvedEvent;
		public readonly PersistentSubscriptionClient HandlingClient;
		public readonly int RetryCount;
		public readonly Guid EventId;
		public readonly bool IsReplayedEvent;
		public readonly long? EventSequenceNumber;

		public OutstandingMessage(Guid eventId, PersistentSubscriptionClient handlingClient,
			ResolvedEvent resolvedEvent, int retryCount, long? eventSequenceNumber) : this() {
			EventId = eventId;
			HandlingClient = handlingClient;
			ResolvedEvent = resolvedEvent;
			RetryCount = retryCount;
			IsReplayedEvent = resolvedEvent.OriginalStreamId.StartsWith("$persistentsubscription-") && resolvedEvent.OriginalStreamId.EndsWith("-parked");
			EventSequenceNumber = eventSequenceNumber;
		}
	}
}
