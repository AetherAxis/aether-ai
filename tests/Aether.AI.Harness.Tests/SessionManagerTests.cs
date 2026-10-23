using Aether.AI.Core.Sessions;
using Aether.AI.Harness.Sessions;

namespace Aether.AI.Harness.Tests;

public class SessionManagerTests
{
    [Fact]
    public void Create_returns_active_session_with_ids()
    {
        var manager = new SessionManager();

        var session = manager.Create("demo-assistant", "Demo");

        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Equal("demo-assistant", session.AgentId);
        Assert.Equal(SessionState.Active, session.State);
    }

    [Fact]
    public void Get_returns_created_session()
    {
        var manager = new SessionManager();
        var session = manager.Create("a", "A");

        var fetched = manager.Get(session.SessionId);

        Assert.Same(session, fetched);
    }

    [Fact]
    public void Remove_deletes_session()
    {
        var manager = new SessionManager();
        var session = manager.Create("a", "A");

        Assert.True(manager.Remove(session.SessionId));
        Assert.Null(manager.Get(session.SessionId));
    }
}
