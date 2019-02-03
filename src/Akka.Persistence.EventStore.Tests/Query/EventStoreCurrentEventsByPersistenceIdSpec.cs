using System;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence.Query;
using Akka.Persistence.EventStore.Query;
using Akka.Persistence.TCK.Query;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.Util.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Persistence.EventStore.Tests.Query
{
    [Collection("EventStoreCurrentEventsByPersistenceIdSpec")]
    public class EventStoreCurrentEventsByPersistenceIdSpec : CurrentEventsByPersistenceIdSpec,
            IClassFixture<DatabaseFixture>
    {
        private static Config Config(DatabaseFixture databaseFixture)
        {
            return ConfigurationFactory.ParseString($@"
				akka.loglevel = INFO
                akka.persistence.journal.plugin = ""akka.persistence.journal.eventstore""
                akka.persistence.journal.eventstore {{
                    class = ""Akka.Persistence.EventStore.Journal.EventStoreJournal, Akka.Persistence.EventStore""
                    connection-string = ""{databaseFixture.ConnectionString}""
                    connection-name = ""{nameof(EventStoreCurrentEventsByPersistenceIdSpec)}""
                    read-batch-size = 500
                }}
                akka.test.single-expect-default = 10s").WithFallback(EventStoreReadJournal.DefaultConfiguration());
        }

        public EventStoreCurrentEventsByPersistenceIdSpec(DatabaseFixture databaseFixture, ITestOutputHelper output) :
                base(Config(databaseFixture), nameof(EventStoreCurrentEventsByPersistenceIdSpec), output)
        {
            ReadJournal = Sys.ReadJournalFor<EventStoreReadJournal>(EventStoreReadJournal.Identifier);
        }

        [Fact]
        public override void ReadJournal_CurrentEventsByPersistenceId_should_find_existing_events()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("a");

            var src = queries.CurrentEventsByPersistenceId("a", 0, long.MaxValue);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer);
            probe.Request(2)
                 .ExpectNext("a-1", "a-2")
                 .ExpectNoMsg(TimeSpan.FromMilliseconds(500));
            probe.Request(2)
                 .ExpectNext("a-3")
                 .ExpectComplete();
        }

        [Fact]
        public override void
                ReadJournal_CurrentEventsByPersistenceId_should_find_existing_events_up_to_a_sequence_number()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("b");
            var src = queries.CurrentEventsByPersistenceId("b", 0L, 2L);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer)
                           .Request(5)
                           .ExpectNext("b-1", "b-2")
                           .ExpectComplete();
        }

        [Fact]
        public override void ReadJournal_CurrentEventsByPersistenceId_should_not_see_new_events_after_completion()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("f");
            var src = queries.CurrentEventsByPersistenceId("f", 0L, long.MaxValue);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer)
                           .Request(2)
                           .ExpectNext("f-1", "f-2")
                           .ExpectNoMsg(TimeSpan.FromMilliseconds(100)) as TestSubscriber.Probe<object>;

            pref.Tell("f-4");
            ExpectMsg("f-4-done");

            probe.ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            probe.Request(5)
                 .ExpectNext("f-3")
                 .ExpectComplete(); // f-4 not seen
        }

        [Fact]
        public override void
                ReadJournal_CurrentEventsByPersistenceId_should_return_empty_stream_for_cleaned_journal_from_0_to_MaxLong()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("g1");

            pref.Tell(new TestActor.DeleteCommand(3));
            AwaitAssert(() => ExpectMsg("3-deleted"));

            var src = queries.CurrentEventsByPersistenceId("g1", 0, long.MaxValue);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public override void
                ReadJournal_CurrentEventsByPersistenceId_should_return_empty_stream_for_cleaned_journal_from_0_to_0()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("g2");

            pref.Tell(new TestActor.DeleteCommand(3));
            AwaitAssert(() => ExpectMsg("3-deleted"));

            var src = queries.CurrentEventsByPersistenceId("g2", 0, 0);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public override void
                ReadJournal_CurrentEventsByPersistenceId_should_return_remaining_values_after_partial_journal_cleanup()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("h");

            pref.Tell(new TestActor.DeleteCommand(2));
            AwaitAssert(() => ExpectMsg("2-deleted"));

            var src = queries.CurrentEventsByPersistenceId("h", 0L, long.MaxValue);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer)
               .Request(1)
               .ExpectNext("h-3")
               .ExpectComplete();
        }

        [Fact]
        public override void ReadJournal_CurrentEventsByPersistenceId_should_return_empty_stream_for_empty_journal()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = SetupEmpty("i");

            var src = queries.CurrentEventsByPersistenceId("i", 0L, long.MaxValue);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public override void
                ReadJournal_CurrentEventsByPersistenceId_should_return_empty_stream_for_journal_from_0_to_0()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("k1");

            var src = queries.CurrentEventsByPersistenceId("k1", 0, 0);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public override void
                ReadJournal_CurrentEventsByPersistenceId_should_return_empty_stream_for_empty_journal_from_0_to_0()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = SetupEmpty("k2");

            var src = queries.CurrentEventsByPersistenceId("k2", 0, 0);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public override void
                ReadJournal_CurrentEventsByPersistenceId_should_return_empty_stream_for_journal_from_SequenceNr_greater_than_HighestSequenceNr()
        {
            var queries = ReadJournal.AsInstanceOf<ICurrentEventsByPersistenceIdQuery>();
            var pref = Setup("l");

            var src = queries.CurrentEventsByPersistenceId("l", 4L, 3L);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), Materializer).Request(1).ExpectComplete();
        }

        private IActorRef Setup(string persistenceId)
        {
            var pref = SetupEmpty(persistenceId);

            pref.Tell(persistenceId + "-1");
            pref.Tell(persistenceId + "-2");
            pref.Tell(persistenceId + "-3");

            ExpectMsg(persistenceId + "-1-done");
            ExpectMsg(persistenceId + "-2-done");
            ExpectMsg(persistenceId + "-3-done");
            return pref;
        }

        private IActorRef SetupEmpty(string persistenceId)
        {
            return Sys.ActorOf(Query.TestActor.Props(persistenceId));
        }

        protected override void Dispose(bool disposing)
        {
            Materializer.Dispose();
            base.Dispose(disposing);
        }
    }
}