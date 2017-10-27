#!/usr/bin/env bash
mono .paket/paket.exe restore
FACADE_PATH=./paket-files/logary/logary/src/Logary.Facade/Facade.fs
sed -e 's/namespace Logary\.Facade/namespace RouteMaster\.Logging/' $FACADE_PATH > $FACADE_PATH.tmp && mv $FACADE_PATH.tmp $FACADE_PATH
export ROUTEMASTER_EASYNETQ="host=localhost"
export ROUTEMASTER_POSTGRES="host=localhost;database=routemaster_tests;password=routemaster;username=postgres"
dotnet run --no-restore -p ./Tests/Tests.fsproj --sequenced --summary
