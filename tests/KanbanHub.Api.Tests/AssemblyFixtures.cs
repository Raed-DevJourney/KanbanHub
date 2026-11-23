using KanbanHub.TestSupport;

// One database for the whole test run; each test creates its own workspace and board in it.
[assembly: AssemblyFixture(typeof(KanbanDatabase))]
