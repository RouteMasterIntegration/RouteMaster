module RouteMaster.State.PostgreSQL

open System
open System.Linq
open Marten
open Marten.Services
open RouteMaster

let makeId (CorrelationId cid)
           (StepName sn)
           (ProcessId pid) =
    cid + "::" + sn + "::" + pid

type MartenStoredExpect(sid, cid, nss, pid, exp, ts, tss) =
    member val Id : string = sid with get, set
    member val CorrelationString : string = cid with get, set
    member val NextStepString : string = nss with get, set
    member val ProcessString : string = pid with get, set
    member val Expiry : DateTime = exp with get, set
    member val TimeToLive : TimeSpan = ts with get, set
    member val TimeoutStepString : string = tss with get, set
    static member OfStoredExpect (now : unit -> DateTime) (se : StoredExpect) =
        MartenStoredExpect(
            makeId se.CorrelationId se.NextStepName se.ProcessId,
            se.CorrelationId.Value,
            se.NextStepName.Value,
            se.ProcessId.Value,
            (try (now ()) + se.TimeToLive with _ -> DateTime.MaxValue),
            se.TimeToLive,
            se.TimeoutStepName.Value
        )
    static member ToStoredExpect (mse : MartenStoredExpect) =
        { CorrelationId = CorrelationId mse.CorrelationString
          NextStepName = StepName mse.NextStepString
          TimeoutStepName = StepName mse.TimeoutStepString
          TimeToLive = mse.TimeToLive
          ProcessId = ProcessId mse.ProcessString }

type MartenExpectedStore(store : IDocumentStore, now) =
    new (store) = MartenExpectedStore(store, fun () -> DateTime.Now)
    interface ExpectedStore with
        member __.Add se =
            async {
                use session = store.LightweightSession()
                session.Store(MartenStoredExpect.OfStoredExpect now se)
                return! session.SaveChangesAsync() |> Async.AwaitTask
            }
        member __.Remove cid stepName processId =
            async {
                use session = store.LightweightSession()
                let storedId = makeId cid stepName processId
                session.Delete<MartenStoredExpect> storedId
                return! session.SaveChangesAsync() |> Async.AwaitTask
            }
        member __.GetProcessId (CorrelationId cid) (StepName stepName) =
            async {
                use session = store.LightweightSession()
                let! stored =
                    session.Query<MartenStoredExpect>()
                        .Where(fun mse ->
                               mse.CorrelationString = cid
                               && mse.NextStepString = stepName)
                        .ToListAsync()
                    |> Async.AwaitTask
                match stored with
                | s when s.IsEmpty() -> return None
                | s -> return Some (s.First().ProcessString |> ProcessId)
            }
        member __.IsActive (ProcessId processId) =
            async {
                use session = store.LightweightSession()
                try
                    return!
                        session.Query<MartenStoredExpect>()
                            .Where(fun mse -> mse.ProcessString = processId)
                            .AnyAsync()
                        |> Async.AwaitTask
                with
                | _ -> return false
            }
        member __.Cancel (ProcessId processId) =
            async {
                use session = store.LightweightSession()
                let! expected =
                    session.Query<MartenStoredExpect>()
                        .Where(fun mse -> mse.ProcessString = processId)
                        .ToListAsync()
                    |> Async.AwaitTask
                for exp in expected do
                    session.Delete<MartenStoredExpect> exp.Id
                return! session.SaveChangesAsync() |> Async.AwaitTask
            }
        member __.TimedOut timeoutFunc =
            async {
                use session = store.LightweightSession()
                let rightNow = now()
                let! expired =
                    session.Query<MartenStoredExpect>()
                        .Where(fun mse -> mse.Expiry < rightNow)
                        .ToListAsync()
                    |> Async.AwaitTask
                for exp in expired |> Seq.map MartenStoredExpect.ToStoredExpect do
                    do!
                        { CorrelationId = exp.CorrelationId
                          TimeoutStepName = exp.TimeoutStepName
                          ExpectedStepName = exp.NextStepName
                          ProcessId = exp.ProcessId }
                        |> timeoutFunc
                    session.Delete<MartenStoredExpect>
                        (makeId
                            exp.CorrelationId
                            exp.NextStepName
                            exp.ProcessId)
                return! session.SaveChangesAsync() |> Async.AwaitTask
            }

type MartenStoredState<'state> =
    { Id : string
      State : 'state }

type MartenStateStore(store : IDocumentStore) =
    interface StateStore with
        member __.Create (ProcessId processId) value =
            use session = store.LightweightSession()
            session.Store { Id = processId; State = value }
            session.SaveChanges()
        member __.Access (ProcessId processId) : StateAccess<'a> =
            { new StateAccess<'a> with
                member x.Update updateFunc =
                    try
                        use session = store.LightweightSession(Data.IsolationLevel.Serializable)
                        let prev = session.Load<MartenStoredState<'a>> processId
                        if isNull <| box prev then
                            None
                        else
                            let next =
                                { prev with State = updateFunc prev.State }
                            session.Update<MartenStoredState<'a>> next
                            session.SaveChanges()
                            Some next.State
                     with
                     | :? ConcurrentUpdateException as e ->
                             x.Update updateFunc
                     }
        member __.Remove<'state when 'state : not struct> (ProcessId processId) =
            use session = store.OpenSession(DocumentTracking.None)
            session.Delete<MartenStoredState<'state>> processId
            session.SaveChanges()

