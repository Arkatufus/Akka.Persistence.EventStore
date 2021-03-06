﻿using System;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence.EventStore.Query.Publishers;
using Akka.Persistence.Journal;
using Akka.Persistence.Query;
using Akka.Streams.Dsl;
using EventStore.ClientAPI;

namespace Akka.Persistence.EventStore.Query
{
    /// <inheritdoc />
    public class EventStoreReadJournal : IReadJournal,
            IPersistenceIdsQuery,
            ICurrentPersistenceIdsQuery,
            IEventsByPersistenceIdQuery,
            ICurrentEventsByPersistenceIdQuery,
            IEventsByTagQuery,
            ICurrentEventsByTagQuery
    {
        /// <summary>
        /// HOCON identifier
        /// </summary>
        public const string Identifier = "akka.persistence.query.journal.eventstore";

        private readonly string _writeJournalPluginId;
        private readonly int _maxBufferSize;
        private readonly bool _autoAck;


        /// <inheritdoc />
        public EventStoreReadJournal(ExtendedActorSystem system, Config config)
        {
            _writeJournalPluginId = config.GetString("write-plugin");
            _maxBufferSize = config.GetInt("max-buffer-size");
            _autoAck = config.GetBoolean("auto-ack");
        }

        /// <summary>
        /// Returns a default query configuration for akka persistence SQLite-based journals and snapshot stores.
        /// </summary>
        /// <returns></returns>
        public static Config DefaultConfiguration()
        {
            return ConfigurationFactory.FromResource<EventStoreReadJournal>(
                "Akka.Persistence.EventStore.Query.reference.conf");
        }

        /// <summary>
        /// Query all <see cref="T:Akka.Persistence.PersistentActor" /> identifiers, i.e. as defined by the
        /// `persistenceId` of the <see cref="T:Akka.Persistence.PersistentActor" />.
        /// 
        /// The stream is not completed when it reaches the end of the currently used `persistenceIds`,
        /// but it continues to push new `persistenceIds` when new persistent actors are created.
        /// Corresponding query that is completed when it reaches the end of the currently
        /// used `persistenceIds` is provided by <see cref="M:Akka.Persistence.Query.ICurrentPersistenceIdsQuery.CurrentPersistenceIds" />.
        ///
        /// *Please note*, to use this feature, you need to enable `$streams` built-in projection in EventStore server. Please refer to
        /// EventStore server documentation to find out how.
        /// </summary>
        public Source<string, NotUsed> PersistenceIds()
        {
            var publisherProps = AllPersistenceIdsPublisher.Props(true, _writeJournalPluginId);
            return Source
                   .ActorPublisher<string>(publisherProps)
                   .MapMaterializedValue(_ => NotUsed.Instance)
                   .Named("AllPersistenceIds");
        }

        /// <summary>
        /// Same type of query as <see cref="PersistenceIds"/> but the stream
        /// is completed immediately when it reaches the end of the "result set". Persistent
        /// actors that are created after the query is completed are not included in the stream.
        /// </summary>
        public Source<string, NotUsed> CurrentPersistenceIds() =>
                Source.ActorPublisher<string>(AllPersistenceIdsPublisher.Props(false, _writeJournalPluginId))
                      .MapMaterializedValue(_ => NotUsed.Instance)
                      .Named("CurrentPersistenceIds");

        /// <summary>
        /// <see cref="EventsByPersistenceId"/> is used for retrieving events for a specific
        /// <see cref="PersistentActor"/> identified by <see cref="Eventsourced.PersistenceId"/>.
        /// <para>
        /// You can retrieve a subset of all events by specifying <paramref name="fromSequenceNr"/> and <paramref name="toSequenceNr"/>
        /// or use `0L` and <see cref="long.MaxValue"/> respectively to retrieve all events. Note that
        /// the corresponding sequence number of each event is provided in the
        /// <see cref="EventEnvelope"/>, which makes it possible to resume the
        /// stream at a later point from a given sequence number.
        /// </para>
        /// The returned event stream is ordered by sequence number, i.e. the same order as the
        /// <see cref="PersistentActor"/> persisted the events. The same prefix of stream elements (in same order)
        ///  are returned for multiple executions of the query, except for when events have been deleted.
        /// <para>
        /// The stream is not completed when it reaches the end of the currently stored events,
        /// but it continues to push new events when new events are persisted.
        /// Corresponding query that is completed when it reaches the end of the currently
        /// stored events is provided by <see cref="CurrentEventsByPersistenceId"/>.
        /// </para>
        /// The SQLite write journal is notifying the query side as soon as events are persisted, but for
        /// efficiency reasons the query side retrieves the events in batches that sometimes can
        /// be delayed up to the configured `refresh-interval`.
        /// <para></para>
        /// The stream is completed with failure if there is a failure in executing the query in the
        /// backend journal.
        /// </summary>
        public Source<EventEnvelope, NotUsed> EventsByPersistenceId(string persistenceId, long fromSequenceNr,
            long toSequenceNr)
        {
            var props = EventsByPersistenceIdPublisher.Props(
                persistenceId,
                fromSequenceNr,
                toSequenceNr,
                _maxBufferSize,
                _writeJournalPluginId,
                true
            );
            return Source.ActorPublisher<EventEnvelope>(props)
                         .MapMaterializedValue(_ => NotUsed.Instance)
                         .Named("EventsByPersistenceId-" + persistenceId);
        }


        /// <summary>
        /// Same type of query as <see cref="EventsByPersistenceId"/> but the event stream
        /// is completed immediately when it reaches the end of the "result set". Events that are
        /// stored after the query is completed are not included in the event stream.
        /// </summary>
        public Source<EventEnvelope, NotUsed> CurrentEventsByPersistenceId(
            string persistenceId, long fromSequenceNr, long toSequenceNr
        )
        {
            var props = EventsByPersistenceIdPublisher.Props(
                persistenceId,
                fromSequenceNr,
                toSequenceNr,
                _maxBufferSize,
                _writeJournalPluginId,
                false
            );
            return Source.ActorPublisher<EventEnvelope>(props)
                         .MapMaterializedValue(_ => NotUsed.Instance)
                         .Named("CurrentEventsByPersistenceId-" + persistenceId);
        }

        /// <summary>
        /// <see cref="EventsByTag"/> is used for retrieving events that were marked with
        /// a given tag, e.g. all events of an Aggregate Root type.
        /// <para></para>
        /// To tag events you create an <see cref="IEventAdapter"/> that wraps the events
        /// in a <see cref="Tagged"/> with the given `tags`.
        /// <para></para>
        /// You can use <see cref="NoOffset"/> to retrieve all events with a given tag or retrieve a subset of all
        /// events by specifying a <see cref="Sequence"/>. The `offset` corresponds to an ordered sequence number for
        /// the specific tag. Note that the corresponding offset of each event is provided in the
        /// <see cref="EventEnvelope"/>, which makes it possible to resume the
        /// stream at a later point from a given offset.
        /// <para></para>
        /// The `offset` is exclusive, i.e. the event with the exact same sequence number will not be included
        /// in the returned stream.This means that you can use the offset that is returned in <see cref="EventEnvelope"/>
        /// as the `offset` parameter in a subsequent query.
        /// <para></para>
        /// In addition to the <paramref name="offset"/> the <see cref="EventEnvelope"/> also provides `persistenceId` and `sequenceNr`
        /// for each event. The `sequenceNr` is the sequence number for the persistent actor with the
        /// `persistenceId` that persisted the event. The `persistenceId` + `sequenceNr` is an unique
        /// identifier for the event.
        /// <para></para>
        /// The returned event stream is ordered by the offset (tag sequence number), which corresponds
        /// to the same order as the write journal stored the events. The same stream elements (in same order)
        /// are returned for multiple executions of the query. Deleted events are not deleted from the
        /// tagged event stream.
        /// <para></para>
        /// The stream is not completed when it reaches the end of the currently stored events,
        /// but it continues to push new events when new events are persisted.
        /// Corresponding query that is completed when it reaches the end of the currently
        /// stored events is provided by <see cref="CurrentEventsByTag"/>.
        /// <para></para>
        /// The SQL write journal is notifying the query side as soon as tagged events are persisted, but for
        /// efficiency reasons the query side retrieves the events in batches that sometimes can
        /// be delayed up to the configured `refresh-interval`.
        /// <para></para>
        /// The stream is completed with failure if there is a failure in executing the query in the
        /// backend journal.
        /// </summary>
        public Source<EventEnvelope, NotUsed> EventsByTag(string tag, Offset offset = null)
        {
            offset = offset ?? new Sequence(StreamPosition.Start);
            switch (offset)
            {
                case Sequence seq:
                    return GetCurrentEventsByTag(tag, seq.Value, true);
                case NoOffset _:
                    return GetCurrentEventsByTag(tag, null, true);
                default:
                    throw new ArgumentException($"SqlReadJournal does not support {offset.GetType().Name} offsets");
            }
        }

        /// <summary>
        /// Same type of query as <see cref="EventsByTag"/> but the event stream
        /// is completed immediately when it reaches the end of the "result set". Events that are
        /// stored after the query is completed are not included in the event stream.
        /// </summary>
        /// <param name="offset">Zero-based Akka index</param>
        public Source<EventEnvelope, NotUsed> CurrentEventsByTag(string tag, Offset offset = null)
        {
            offset = offset ?? Offset.NoOffset();
            switch (offset)
            {
                case Sequence seq:
                    return GetCurrentEventsByTag(tag, seq.Value, false);
                case NoOffset _:
                    return GetCurrentEventsByTag(tag, null, false);
                default:
                    throw new ArgumentException($"SqlReadJournal does not support {offset.GetType().Name} offsets");
            }
        }

        private Source<EventEnvelope, NotUsed> GetCurrentEventsByTag(string tag, long? offset, bool isLive)
        {
            var props = EventsByTagPublisher.Props(
                                    tag,
                                    isLive,
                                    offset,
                                    long.MaxValue,
                                    _maxBufferSize,
                                    _writeJournalPluginId
                                );
            return Source.ActorPublisher<EventEnvelope>(props)
                         .MapMaterializedValue(_ => NotUsed.Instance)
                         .Named($"CurrentEventsByTag-{tag}");
        }
    }
}