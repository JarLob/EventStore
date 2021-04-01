﻿using System;
using System.Collections.Generic;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.LogAbstraction;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.Services.Storage.ReaderIndex {
	public interface IAllReader {
		/// <summary>
		/// Returns event records in the sequence they were committed into TF.
		/// Positions is specified as pre-positions (pointer at the beginning of the record).
		/// </summary>
		IndexReadAllResult ReadAllEventsForward(TFPos pos, int maxCount);

		/// <summary>
		/// Returns event records whose eventType matches the given <see cref="EventFilter"/> in the sequence they were committed into TF.
		/// Positions is specified as pre-positions (pointer at the beginning of the record).
		/// </summary>
		IndexReadAllResult FilteredReadAllEventsForward(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter);

		/// <summary>
		/// Returns event records in the reverse sequence they were committed into TF.
		/// Positions is specified as post-positions (pointer after the end of record).
		/// </summary>
		IndexReadAllResult ReadAllEventsBackward(TFPos pos, int maxCount);

		/// <summary>
		/// Returns event records whose eventType matches the given <see cref="EventFilter"/> in the sequence they were committed into TF.
		/// Positions is specified as pre-positions (pointer at the beginning of the record).
		/// </summary>
		IndexReadAllResult FilteredReadAllEventsBackward(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter);
	}

	public class AllReader<TStreamId> : IAllReader {
		private readonly IIndexBackend _backend;
		private readonly IIndexCommitter _indexCommitter;
		private readonly IStreamIdToName<TStreamId> _streamNames;

		public AllReader(IIndexBackend backend, IIndexCommitter indexCommitter, IStreamIdToName<TStreamId> streamNames) {
			Ensure.NotNull(backend, "backend");
			Ensure.NotNull(indexCommitter, "indexCommitter");
			Ensure.NotNull(indexCommitter, nameof(streamNames));
			_backend = backend;
			_indexCommitter = indexCommitter;
			_streamNames = streamNames;
		}

		public IndexReadAllResult ReadAllEventsForward(TFPos pos, int maxCount) {
			return ReadAllEventsForwardInternal(pos, maxCount, maxCount, EventFilter.None);
		}

		public IndexReadAllResult FilteredReadAllEventsForward(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter) {
			return ReadAllEventsForwardInternal(pos, maxCount, maxSearchWindow, eventFilter);
		}


		private IndexReadAllResult ReadAllEventsForwardInternal(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter) {
			var records = new List<CommitEventRecord>();
			var nextPos = pos;
			// in case we are at position after which there is no commit at all, in that case we have to force 
			// PreparePosition to long.MaxValue, so if you decide to read backwards from PrevPos, 
			// you will receive all prepares.
			var prevPos = new TFPos(pos.CommitPosition, long.MaxValue);
			var consideredEventsCount = 0L;
			var firstCommit = true;
			var reachedEndOfStream = false;
			using (var reader = _backend.BorrowReader()) {
				long nextCommitPos = pos.CommitPosition;
				while (records.Count < maxCount && consideredEventsCount < maxSearchWindow) {
					if (nextCommitPos > _indexCommitter.LastIndexedPosition) {
						reachedEndOfStream = true;
						break;
					}

					reader.Reposition(nextCommitPos);

					SeqReadResult result;
					while ((result = reader.TryReadNext()).Success && !IsCommitAlike(result.LogRecord)) {
						// skip until commit
					}

					if (!result.Success) // no more records in TF
						break;

					nextCommitPos = result.RecordPostPosition;

					switch (result.LogRecord.RecordType) {
						case LogRecordType.Prepare: {
							var prepare = (IPrepareLogRecord<TStreamId>)result.LogRecord;
							if (firstCommit) {
								firstCommit = false;
								prevPos = new TFPos(result.RecordPrePosition, result.RecordPrePosition);
							}

							if (prepare.Flags.HasAnyOf(PrepareFlags.Data | PrepareFlags.StreamDelete)
							    && new TFPos(prepare.LogPosition, prepare.LogPosition) >= pos) {
								var streamName = _streamNames.LookupName(prepare.EventStreamId);
								var eventRecord = new EventRecord(eventNumber: prepare.ExpectedVersion + 1,
									prepare, streamName);
								consideredEventsCount++;
								if (eventFilter.IsEventAllowed(eventRecord)) {
									records.Add(new CommitEventRecord(eventRecord, prepare.LogPosition));
								}

								nextPos = new TFPos(result.RecordPostPosition, 0);
							}

							break;
						}

						case LogRecordType.Commit: {
							var commit = (CommitLogRecord)result.LogRecord;
							if (firstCommit) {
								firstCommit = false;
								// for backward pass we want to allow read the same commit and skip read prepares, 
								// so we put post-position of commit and post-position of prepare as TFPos for backward pass
								prevPos = new TFPos(result.RecordPostPosition, pos.PreparePosition);
							}

							reader.Reposition(commit.TransactionPosition);
							while (consideredEventsCount < maxCount) {
								result = reader.TryReadNext();
								if (!result.Success) // no more records in TF
									break;
								// prepare with TransactionEnd could be scavenged already
								// so we could reach the same commit record. In that case have to stop
								if (result.LogRecord.LogPosition >= commit.LogPosition)
									break;
								if (result.LogRecord.RecordType != LogRecordType.Prepare)
									continue;

								var prepare = (IPrepareLogRecord<TStreamId>)result.LogRecord;
								if (prepare.TransactionPosition != commit.TransactionPosition) // wrong prepare
									continue;

								// prepare with useful data or delete tombstone
								if (prepare.Flags.HasAnyOf(PrepareFlags.Data | PrepareFlags.StreamDelete)
								    && new TFPos(commit.LogPosition, prepare.LogPosition) >= pos) {
									var streamName = _streamNames.LookupName(prepare.EventStreamId);
									var eventRecord =
										new EventRecord(commit.FirstEventNumber + prepare.TransactionOffset,
											prepare, streamName);
									consideredEventsCount++;
									if (eventFilter.IsEventAllowed(eventRecord)) {
										records.Add(new CommitEventRecord(eventRecord, commit.LogPosition));
									}

									// for forward pass position is inclusive, 
									// so we put pre-position of commit and post-position of prepare
									nextPos = new TFPos(commit.LogPosition, result.RecordPostPosition);
								}

								if (prepare.Flags.HasAnyOf(PrepareFlags.TransactionEnd))
									break;
							}

							break;
						}

						default:
							throw new Exception(string.Format("Unexpected log record type: {0}.",
								result.LogRecord.RecordType));
					}
				}

				return new IndexReadAllResult(records, pos, nextPos, prevPos, reachedEndOfStream, consideredEventsCount);
			}
		}
		
		public IndexReadAllResult ReadAllEventsBackward(TFPos pos, int maxCount) {
			return ReadAllEventsBackwardInternal(pos, maxCount, maxCount, EventFilter.None);
		}

		public IndexReadAllResult FilteredReadAllEventsBackward(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter) {
			return ReadAllEventsBackwardInternal(pos, maxCount, maxSearchWindow, eventFilter);
		}

		private IndexReadAllResult ReadAllEventsBackwardInternal(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter) {
			var records = new List<CommitEventRecord>();
			var nextPos = pos;
			// in case we are at position after which there is no commit at all, in that case we have to force 
			// PreparePosition to 0, so if you decide to read backwards from PrevPos, 
			// you will receive all prepares.
			var prevPos = new TFPos(pos.CommitPosition, 0);
			var consideredEventsCount = 0L;
			bool firstCommit = true;
			var reachedEndOfStream = false;
			using (var reader = _backend.BorrowReader()) {
				long nextCommitPostPos = pos.CommitPosition;
				while (records.Count < maxCount && consideredEventsCount < maxSearchWindow) {
					reader.Reposition(nextCommitPostPos);

					SeqReadResult result;
					while ((result = reader.TryReadPrev()).Success && !IsCommitAlike(result.LogRecord)) {
						// skip until commit
					}

					if (!result.Success) {
						// no more records in TF
						reachedEndOfStream = true;
						break;
					}

					nextCommitPostPos = result.RecordPrePosition;

					if (nextCommitPostPos > _indexCommitter.LastIndexedPosition) {
						continue;
					}

					switch (result.LogRecord.RecordType) {
						case LogRecordType.Prepare: {
							var prepare = (IPrepareLogRecord<TStreamId>)result.LogRecord;
							if (firstCommit) {
								firstCommit = false;
								prevPos = new TFPos(result.RecordPostPosition, result.RecordPostPosition);
							}

							if (prepare.Flags.HasAnyOf(PrepareFlags.Data | PrepareFlags.StreamDelete)
							    && new TFPos(result.RecordPostPosition, result.RecordPostPosition) <= pos) {
								var streamName = _streamNames.LookupName(prepare.EventStreamId);
								var eventRecord = new EventRecord(eventNumber: prepare.ExpectedVersion + 1,
									prepare, streamName);
								consideredEventsCount++;

								if (eventFilter.IsEventAllowed(eventRecord)) {
									records.Add(new CommitEventRecord(eventRecord, prepare.LogPosition));
								}

								// for backward pass we allow read the same commit, but force to skip last read prepare
								// so we put post-position of commit and pre-position of prepare
								nextPos = new TFPos(result.RecordPrePosition, result.RecordPrePosition);
							}

							break;
						}

						case LogRecordType.Commit: {
							var commit = (CommitLogRecord)result.LogRecord;
							if (firstCommit) {
								firstCommit = false;
								// for forward pass we allow read the same commit and as we have post-positions here
								// we can put just prepare post-position as prepare pre-position for forward read
								// so we put pre-position of commit and post-position of prepare
								prevPos = new TFPos(commit.LogPosition, pos.PreparePosition);
							}

							var commitPostPos = result.RecordPostPosition;
							// as we don't know exact position of the last record of transaction,
							// we have to sequentially scan backwards, so no need to reposition
							while (consideredEventsCount < maxCount) {
								result = reader.TryReadPrev();
								if (!result.Success) // no more records in TF
									break;

								// prepare with TransactionBegin could be scavenged already
								// so we could reach beyond the start of transaction. In that case we have to stop.
								if (result.LogRecord.LogPosition < commit.TransactionPosition)
									break;
								if (result.LogRecord.RecordType != LogRecordType.Prepare)
									continue;

								var prepare = (IPrepareLogRecord<TStreamId>)result.LogRecord;
								if (prepare.TransactionPosition != commit.TransactionPosition) // wrong prepare
									continue;

								// prepare with useful data or delete tombstone
								if (prepare.Flags.HasAnyOf(PrepareFlags.Data | PrepareFlags.StreamDelete)
								    && new TFPos(commitPostPos, result.RecordPostPosition) <= pos) {
									var streamName = _streamNames.LookupName(prepare.EventStreamId);
									var eventRecord =
										new EventRecord(commit.FirstEventNumber + prepare.TransactionOffset,
											prepare, streamName);
									consideredEventsCount++;

									if (eventFilter.IsEventAllowed(eventRecord)) {
										records.Add(new CommitEventRecord(eventRecord, commit.LogPosition));
									}

									// for backward pass we allow read the same commit, but force to skip last read prepare
									// so we put post-position of commit and pre-position of prepare
									nextPos = new TFPos(commitPostPos, prepare.LogPosition);
								}

								if (prepare.Flags.HasAnyOf(PrepareFlags.TransactionBegin))
									break;
							}

							break;
						}

						default:
							throw new Exception(string.Format("Unexpected log record type: {0}.",
								result.LogRecord.RecordType));
					}
				}

				return new IndexReadAllResult(records, pos, nextPos, prevPos, reachedEndOfStream, consideredEventsCount);
			}
		}

		private static bool IsCommitAlike(ILogRecord rec) {
			return rec.RecordType == LogRecordType.Commit
			       || (rec.RecordType == LogRecordType.Prepare &&
			           ((IPrepareLogRecord)rec).Flags.HasAnyOf(PrepareFlags.IsCommitted));
		}
	}
}
