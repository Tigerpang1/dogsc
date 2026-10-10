using FrameTrail;

internal static class HistoryPersistenceProbe
{
    private sealed record State(int Value);
    public static void Run()
    {
        var history = new UndoRedoHistory<State>(new(1), value => value with { });
        history.Commit(new(2));
        history.Commit(new(3));
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
        void Reject(State _) => throw new IOException("Expected storage failure");
        try { history.Undo(Reject); throw new Exception("failed Undo accepted"); } catch (IOException) { }
        Check(history.Current.Value == 3 && history.CanUndo && !history.CanRedo, "failed undo preserves history");
        var persisted = 0;
        Check(history.Undo(s => persisted = s.Value).Value == 2 && persisted == 2, "retry undo persists expected snapshot");
        try { history.Redo(Reject); throw new Exception("failed Redo accepted"); } catch (IOException) { }
        Check(history.Current.Value == 2 && history.CanUndo && history.CanRedo, "failed redo preserves both stacks");
        Check(history.Redo(s => persisted = s.Value).Value == 3 && persisted == 3, "retry redo persists expected snapshot");
        Check(history.Undo().Value == 2 && history.Undo().Value == 1 && !history.CanUndo, "failed persistence does not consume older history");
        Check(history.Redo().Value == 2 && history.Redo().Value == 3 && !history.CanRedo, "redo order survives failure and retry");
        Console.WriteLine("History persistence failure and retry: PASS");
    }
}
