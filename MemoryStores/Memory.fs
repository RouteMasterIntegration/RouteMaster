module RouteMaster.State.Memory

open System
open RouteMaster

type private StoreMsg =
    | Create of ProcessId * obj
    | Update of ProcessId * (obj -> obj) * AsyncReplyChannel<obj option>
    | Remove of ProcessId

let private storeLoop (agent : MailboxProcessor<StoreMsg>) =
    let rec inner contents =
        async {
            let! msg = agent.Receive()
            match msg with
            | Create (processId, value) ->
                return! inner (Map.add processId value contents)
            | Update (processId, update, reply) ->
                try
                    let newValue = update (contents.[processId])
                    reply.Reply (Some newValue)
                    return! inner (Map.add processId newValue contents)
                with
                | _ ->
                    reply.Reply None
                    return! inner contents
            | Remove processId ->
                return! inner (Map.remove processId contents)
        }
    inner Map.empty

type MemoryStore () =
    let agent = MailboxProcessor.Start storeLoop
    do agent.Error.Add raise
    interface StateStore with
        member __.Create<'a when 'a : not struct> processId (value : 'a) =
            agent.Post (Create (processId, box value))
        member __.Access<'a when 'a : not struct> processId =
            { new StateAccess<'a> with
                member x.Update update =
                    let boxed = unbox >> update >> box
                    agent.PostAndReply(fun r -> Update(processId, boxed, r)) |> Option.map unbox }
        member __.Remove processId =
            agent.Post (Remove processId)

type private ExpectedStoreMsg =
    | Add of StoredExpect
    | Remove of CorrelationId * StepName * ProcessId
    | Continuations of CorrelationId * StepName * AsyncReplyChannel<ProcessId option>
    | IsActive of ProcessId * AsyncReplyChannel<bool>
    | Cancel of ProcessId
    | Expire of (TimedOut -> Async<unit>) * AsyncReplyChannel<unit>

let private expectedLoop (now : unit -> DateTime) (agent : MailboxProcessor<ExpectedStoreMsg>) =
    let rec inner expected =
        async {
            let! msg = agent.Receive()
            let! newExpected =
                match msg with
                | Add expectedMessage ->
                    let cid = expectedMessage.CorrelationId
                    let expiryTime =
                        try now() + expectedMessage.TimeToLive with _ -> DateTime.MaxValue
                    let store = expectedMessage, expiryTime
                    if Map.containsKey cid expected then
                        async { return Map.add cid (store::expected.[cid]) expected }
                    else
                        async { return Map.add cid [store] expected }
                | Remove (cid, contId, processId) ->
                    let exs =
                        Map.tryFind cid expected
                        |> Option.map (List.filter (fun e -> (fst e).NextStepName <> contId))
                    match exs with
                    | Some [] ->
                        async { return Map.remove cid expected }
                    | Some exs ->
                        async { return Map.add cid exs expected }
                    | None ->
                        async { return expected }
                | Continuations (cid, contId, reply) ->
                    match Map.tryFind cid expected with
                    | Some exs ->
                        exs
                        |> List.tryFind (fun e -> (fst e).NextStepName = contId)
                        |> Option.map (fun e -> (fst e).ProcessId)
                        |> reply.Reply
                    | None ->
                        reply.Reply None
                    async { return expected }
                | IsActive (processId, reply) ->
                    Map.toSeq expected
                    |> Seq.collect snd
                    |> Seq.exists (fun e -> (fst e).ProcessId = processId)
                    |> reply.Reply
                    async { return expected }
                | Cancel processId ->
                    Map.toSeq expected
                    |> Seq.map (fun (cid, exs) ->
                    cid, exs |> List.filter (fun e -> (fst e).ProcessId <> processId))
                    |> Seq.filter (snd >> List.isEmpty >> not)
                    |> Map.ofSeq
                    |> fun e -> async { return e }
                | Expire (timeOutFunc, r) ->
                    async {
                        let stillExpected, expired =
                            expected
                            |> Map.toList
                            |> List.collect
                                (fun (key, exs) -> exs |> List.map (fun ex -> key, ex))
                            |> List.partition (fun (_, (_, expiry)) -> expiry > now ())

                        let newExpected =
                            stillExpected
                            |> List.groupBy fst
                            |> List.map (fun (k, vs) -> k, vs |> List.map snd)
                            |> Map.ofSeq
                        let expiring =
                            expired
                            |> List.map (fun (_, (ex, _)) ->
                                        { CorrelationId = ex.CorrelationId
                                          TimeoutStepName = ex.TimeoutStepName
                                          ExpectedStepName = ex.NextStepName
                                          ProcessId = ex.ProcessId })
                        for ex in expiring do
                            do! timeOutFunc ex

                        return newExpected
                    }

            return! inner newExpected
        }
    inner Map.empty

type MemoryExpectedStore () =
    let agent = MailboxProcessor.Start (expectedLoop <| fun () -> DateTime.UtcNow)
    do agent.Error.Add raise
    interface ExpectedStore with
        member __.Add expectedMessage =
            async { return agent.Post (Add expectedMessage) }
        member __.Remove cid stepName processId =
            async { agent.Post (Remove (cid, stepName, processId)) }
        member __.GetProcessId cid contId =
            async { return agent.PostAndReply (fun r -> Continuations (cid, contId, r)) }
        member __.IsActive processId =
            async { return agent.PostAndReply (fun r -> IsActive (processId, r)) }
        member __.Cancel processId =
            async { agent.Post (Cancel processId) }
        member __.TimedOut timeOutFunc =
            async {
                return agent.PostAndReply (fun r -> Expire (timeOutFunc, r))
            }
