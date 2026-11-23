#!/bin/sh
# Builds and runs the BeaconCallbacks decoder tests with mono (no Windows needed).
set -e
cd "$(dirname "$0")/.."
mkdir -p bin
mcs -unsafe -nologo -out:bin/beacon_callbacks_tests.exe Tests/BeaconCallbacksTests.cs CoffLoader/src/BeaconCallbacks.cs
mono bin/beacon_callbacks_tests.exe
