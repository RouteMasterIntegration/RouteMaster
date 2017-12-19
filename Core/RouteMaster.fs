namespace RouteMaster

open System
open RouteMaster

type RouteMaster<'state when 'state : not struct> internal (config, starter) =
    let t = TimeoutManager.startTimeoutManager config
    member x.ActiveConfig : Config = config
    member x.Starter : 'state -> Async<StepResult<'state>> = starter
    interface IDisposable with
        member x.Dispose() =
            t.Post TimeoutManager.Stop

