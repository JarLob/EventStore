using System;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Plugins.Authorization;
using Grpc.Core;
using static EventStore.Core.Messages.ClientMessage.DeletePersistentSubscriptionToStreamCompleted;

namespace EventStore.Core.Services.Transport.Grpc {
	public partial class PersistentSubscriptions {
		private static readonly Operation DeleteOperation = new Operation(Plugins.Authorization.Operations.Subscriptions.Delete);
		public override async Task<DeleteResp> Delete(DeleteReq request, ServerCallContext context) {
			
			var createPersistentSubscriptionSource = new TaskCompletionSource<DeleteResp>();
			var correlationId = Guid.NewGuid();

			var user = context.GetHttpContext().User;

			if (!await _authorizationProvider.CheckAccessAsync(user,
				DeleteOperation, context.CancellationToken).ConfigureAwait(false)) {
				throw AccessDenied();
			}

			_publisher.Publish(new ClientMessage.DeletePersistentSubscriptionToStream(
				correlationId,
				correlationId,
				new CallbackEnvelope(HandleDeletePersistentSubscriptionCompleted),
				request.Options.StreamIdentifier,
				request.Options.GroupName,
				user));

			return await createPersistentSubscriptionSource.Task.ConfigureAwait(false);

			void HandleDeletePersistentSubscriptionCompleted(Message message) {
				if (message is ClientMessage.NotHandled notHandled && RpcExceptions.TryHandleNotHandled(notHandled, out var ex)) {
					createPersistentSubscriptionSource.TrySetException(ex);
					return;
				}

				if (!(message is ClientMessage.DeletePersistentSubscriptionToStreamCompleted completed)) {
					createPersistentSubscriptionSource.TrySetException(
						RpcExceptions.UnknownMessage<ClientMessage.DeletePersistentSubscriptionToStreamCompleted>(message));
					return;
				}

				switch (completed.Result) {
					case DeletePersistentSubscriptionToStreamResult.Success:
						createPersistentSubscriptionSource.TrySetResult(new DeleteResp());
						return;
					case DeletePersistentSubscriptionToStreamResult.Fail:
						createPersistentSubscriptionSource.TrySetException(RpcExceptions.PersistentSubscriptionFailed(request.Options.StreamIdentifier, request.Options.GroupName, completed.Reason));
						return;
					case DeletePersistentSubscriptionToStreamResult.DoesNotExist:
						createPersistentSubscriptionSource.TrySetException(RpcExceptions.PersistentSubscriptionDoesNotExist(request.Options.StreamIdentifier, request.Options.GroupName));
						return;
					case DeletePersistentSubscriptionToStreamResult.AccessDenied:
						createPersistentSubscriptionSource.TrySetException(RpcExceptions.AccessDenied());
						return;
					default:
						createPersistentSubscriptionSource.TrySetException(RpcExceptions.UnknownError(completed.Result));
						return;
				}
			}
		}
	}
}
