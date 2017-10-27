module RouteMaster.Tests.Program
#nowarn "46"

open Expecto

[<EntryPoint>]
let main argv =
    try
        let config =
            match maybeDocStore with
            | Some _ ->
                { defaultConfig with parallel = false }
            | None -> defaultConfig
        Tests.runTestsInAssembly config argv
    finally
        match maybeBus with
        | Some bus -> bus.Dispose()
        | None -> ()
        match maybeDocStore with
        | Some store -> store.Dispose()
        | None -> ()
