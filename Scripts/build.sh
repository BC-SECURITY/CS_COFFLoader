#!/bin/sh

mkdir -p bin

echo "Building the beacon functions"
x86_64-w64-mingw32-gcc -o bin/beacon_compatibility.o -I beacon_object/include/ -Os beacon_object/src/beacon_compatibility.c -lws2_32 -c
if [ $? -ne 0 ]; then echo "ERROR: gcc failed"; exit 1; fi

echo "Embedding beacon blob into Program.cs"
python3 Scripts/embed_blob.py bin/beacon_compatibility.o CoffLoader/Program.cs
if [ $? -ne 0 ]; then echo "ERROR: embed_blob.py failed"; exit 1; fi

echo "Building the Executable"
if command -v mcs >/dev/null 2>&1; then
    if [ $# -eq 0 ]; then
        echo 'Building Release'
        mcs -unsafe -platform:x64 -out:bin/coffloader.exe CoffLoader/Program.cs CoffLoader/src/CoffParser.cs CoffLoader/src/CoffStructs.cs
    else
        echo 'Building DEBUG'
        mcs -unsafe -platform:x64 -out:bin/coffloader.exe -d:DEBUG CoffLoader/Program.cs CoffLoader/src/CoffParser.cs CoffLoader/src/CoffStructs.cs
    fi
    if [ $? -ne 0 ]; then echo "ERROR: mcs compilation failed"; exit 1; fi
elif command -v cmd.exe >/dev/null 2>&1; then
    echo 'Building with dotnet (Windows)'
    cmd.exe /c "dotnet build CoffLoader\\CoffLoader.csproj -c Release -o bin" 2>&1
    if [ $? -ne 0 ]; then echo "ERROR: dotnet build failed"; exit 1; fi
    # Rename to lowercase for consistency
    [ -f bin/CoffLoader.exe ] && mv bin/CoffLoader.exe bin/coffloader.exe 2>/dev/null || true
else
    echo "ERROR: No C# compiler found (mcs or dotnet)"; exit 1
fi
