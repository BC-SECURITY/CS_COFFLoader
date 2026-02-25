#!/usr/bin/env python3
"""
Embed beacon blob into Program.cs

Reads a compiled .o file, base64-encodes it, and replaces the beacon_data
line in Program.cs with the new blob.

Usage: python3 embed_blob.py <beacon.o> <Program.cs>
"""

import sys
import base64
import re

def main():
    if len(sys.argv) != 3:
        print("Usage: python3 embed_blob.py <beacon.o> <Program.cs>")
        sys.exit(1)
    
    beacon_file = sys.argv[1]
    program_file = sys.argv[2]
    
    # Read the .o file and base64-encode it
    try:
        with open(beacon_file, 'rb') as f:
            beacon_bytes = f.read()
    except Exception as e:
        print(f"ERROR: Failed to read {beacon_file}: {e}")
        sys.exit(1)
    
    new_base64 = base64.b64encode(beacon_bytes).decode('ascii')
    
    # Read Program.cs
    try:
        with open(program_file, 'r', encoding='utf-8-sig') as f:
            program_content = f.read()
    except Exception as e:
        print(f"ERROR: Failed to read {program_file}: {e}")
        sys.exit(1)
    
    # Replace the beacon_data line using regex
    # Pattern: matches the line with byte[] beacon_data = Decode("..."); 
    pattern = r'(\s+byte\[\] beacon_data = Decode\(")[^"]*("\);)'
    
    # Use a match object to do the replacement manually
    match = re.search(pattern, program_content)
    if not match:
        print(f"ERROR: No beacon_data line found in {program_file}")
        sys.exit(1)
    
    # Build the new content by concatenating parts
    new_content = (
        program_content[:match.start()] +
        match.group(1) +
        new_base64 +
        match.group(2) +
        program_content[match.end():]
    )
    
    # Write back to Program.cs
    try:
        with open(program_file, 'w', encoding='utf-8') as f:
            f.write(new_content)
    except Exception as e:
        print(f"ERROR: Failed to write {program_file}: {e}")
        sys.exit(1)
    
    print(f"Successfully embedded {len(beacon_bytes)} bytes into {program_file}")
    sys.exit(0)

if __name__ == '__main__':
    main()
