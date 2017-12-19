[<AutoOpen>]
module RouteMaster.Tests.Setup

open System
open RouteMaster
open RouteMaster.State.PostgreSQL
open RouteMaster.State.Memory
open RouteMaster.Transport.EasyNetQ
open RouteMaster.Transport.Memory
open EasyNetQ
open Marten

type LatchCommand<'result> =
    | Wait of AsyncReplyChannel<'result>
    | Complete of 'result

module Latch =
    let complete (latch : MailboxProcessor<LatchCommand<_>>) result =
        latch.Post (Complete result)

    let wait (latch : MailboxProcessor<LatchCommand<_>>) =
        latch.PostAndAsyncReply Wait

    let make () =
        let rec inner channels resultO (agent : MailboxProcessor<LatchCommand<'result>>) =
            async {
                let! msg = agent.Receive()
                match msg with
                | Wait r ->
                    match resultO with
                    | Some result ->
                        r.Reply result
                        return! inner [] (Some result) agent
                    | None ->
                        return! inner (r::channels) None agent
                | Complete result ->
                    match resultO with
                    | None ->
                        for r in channels do
                            r.Reply result
                        return! inner [] (Some result) agent
                    | Some _ ->
                        return! inner channels resultO agent
            }
        let agent = MailboxProcessor.Start (inner [] None)
        // Assume no single test should take more than 10 seconds
        // (especially important for CI builds so they don't hang)
        Async.Start (async { do! Async.Sleep 10000
                             agent.Post <| Complete (Unchecked.defaultof<'result>) })
        agent

let maybeBus =
    match Environment.GetEnvironmentVariable "ROUTEMASTER_EASYNETQ" with
    | s when String.IsNullOrWhiteSpace s ->
        None
    | s ->
        let bus = RabbitHutch.CreateBus s
        new EasyNetQMessageBus(bus)
        :> MessageBus
        |> Some

let maybeDocStore =
    match Environment.GetEnvironmentVariable "ROUTEMASTER_POSTGRES" with
    | s when String.IsNullOrWhiteSpace s ->
        None
    | s ->
        DocumentStore.For(fun options ->
        options.Connection s
        options.CreateDatabasesForTenants(fun c ->
        c.ForTenant()
          .CheckAgainstPgDatabase()
          .WithOwner("postgres")
          .WithEncoding("UTF-8")
          .ConnectionLimit(-1)
        |> ignore))
        |> Some

let getBus () =
    match maybeBus with
    | Some bus -> bus
    | None -> new MemoryBus() :> MessageBus

let getStores () =
    match maybeDocStore with
    | Some docStore ->
        let stateStore = MartenStateStore docStore :> StateStore
        let expectedStore = MartenExpectedStore docStore :> ExpectedStore
        stateStore, expectedStore
    | None ->
        let stateStore = MemoryStore () :> StateStore
        let expectedStore = MemoryExpectedStore () :> ExpectedStore
        stateStore, expectedStore

