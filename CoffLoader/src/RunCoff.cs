﻿using System;
using System.IO;
using System.Text;
using System.Diagnostics;


namespace COFFLoader
{
    public class COFFLoader
    {
        private static string beaconOutputData;
        private static int beaconOutputData_sz = 0;

        public static string RunCoff(string functionName, string coffData, string argDataHex)
        {
            string Result = "";

            try
            {
                Debug.WriteLine("Calling COFFLoader.RunCoff()");

                byte[] functionname = Encoding.ASCII.GetBytes(functionName);
                byte[] coff_data = Decode(coffData);
                string tmp_arg_data = unhexlify(Encoding.Default.GetString(Decode(argDataHex)));
                byte[] arg_data = Encoding.Default.GetBytes(tmp_arg_data);
                byte[] beacon_data = Decode("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AAAAAAAAAAAAADkCAAAAAAAACgAAAAMBFAAAAAAAAAAAAAAAAAAAAAAAAAAAAEQCAAAEAAAAAwAAAAIAAAAAAF4CAAAIAAAAAwAAAAIAAAAAAHoCAAAAAAAAAwAAAAIAX19tYWluAAAAAAAAAAAgAAIAAAAAAJYCAAAAAAAAAAAAAAIAAAAAAKoCAAAAAAAAAAAAAAIAAAAAAL4CAAAAAAAAAAAAAAIAAAAAANICAAAAAAAAAAAAAAIAAAAAAOQCAAAAAAAAAAAAAAIAAAAAAPsCAAAAAAAAAAAAAAIAAAAAABADAAAAAAAAAAAAAAIAAAAAACUDAAAAAAAAAAAAAAIAAAAAAEMDAAAAAAAAAAAAAAIAAAAAAF8DAAAAAAAAAAAAAAIAAAAAAHMDAAAAAAAAAAAAAAIAAAAAAJEDAAAAAAAAAAAAAAIArAMAAC50ZXh0LnN0YXJ0dXAALnhkYXRhLnN0YXJ0dXAALnBkYXRhLnN0YXJ0dXAALnJkYXRhJHp6egBiZWFjb25fY29tcGF0aWJpbGl0eS5jAHN3YXBfZW5kaWFuZXNzAEJlYWNvbkRhdGFQYXJzZQBCZWFjb25EYXRhSW50AEJlYWNvbkRhdGFTaG9ydABCZWFjb25EYXRhTGVuZ3RoAEJlYWNvbkRhdGFFeHRyYWN0AEJlYWNvbkZvcm1hdEFsbG9jAEJlYWNvbkZvcm1hdFJlc2V0AEJlYWNvbkZvcm1hdEZyZWUAQmVhY29uRm9ybWF0QXBwZW5kAEJlYWNvbkZvcm1hdFByaW50ZgBCZWFjb25Gb3JtYXRUb1N0cmluZwBCZWFjb25Gb3JtYXRJbnQAQmVhY29uUHJpbnRmAEJlYWNvbk91dHB1dABCZWFjb25Vc2VUb2tlbgBCZWFjb25SZXZlcnRUb2tlbgBCZWFjb25Jc0FkbWluAEJlYWNvbkdldFNwYXduVG8AQmVhY29uU3Bhd25UZW1wb3JhcnlQcm9jZXNzAEJlYWNvbkluamVjdFByb2Nlc3MAQmVhY29uSW5qZWN0VGVtcG9yYXJ5UHJvY2VzcwBCZWFjb25DbGVhbnVwUHJvY2VzcwB0b1dpZGVDaGFyAEJlYWNvbkdldE91dHB1dERhdGEALnRleHQuc3RhcnR1cAAueGRhdGEuc3RhcnR1cAAucGRhdGEuc3RhcnR1cAAucmRhdGEkenp6AGJlYWNvbl9jb21wYXRpYmlsaXR5X3NpemUAYmVhY29uX2NvbXBhdGliaWxpdHlfb3V0cHV0AGJlYWNvbl9jb21wYXRpYmlsaXR5X29mZnNldABfX2ltcF9NU1ZDUlQkbWVtY3B5AF9faW1wX01TVkNSVCRjYWxsb2MAX19pbXBfTVNWQ1JUJG1lbXNldABfX2ltcF9NU1ZDUlQkZnJlZQBfX2ltcF9NU1ZDUlQkdnNucHJpbnRmAF9faW1wX01TVkNSVCR2cHJpbnRmAF9faW1wX01TVkNSVCRyZWFsbG9jAF9faW1wX0FEVkFQSTMyJFNldFRocmVhZFRva2VuAF9faW1wX0FEVkFQSTMyJFJldmVydFRvU2VsZgBfX2ltcF9NU1ZDUlQkc3RybGVuAF9faW1wX0tFUk5FTDMyJENyZWF0ZVByb2Nlc3NBAF9faW1wX0tFUk5FTDMyJENsb3NlSGFuZGxlAA==");

                // Call coffloader once with beacon_compatibility.o before loading BOF to initialize Beacon* functions
                if (
                    CoffParser.parseCOFF(new byte[] { }, beacon_data, beacon_data.Length, null, 0)
                    == 1
                )
                {
                    CoffParser.CleanUpMemoryAllocations();
                    return "parseCOFF Beacon compat failed: 1";
                }

                Debug.WriteLine(
                    "************************ BEACON PROCESSING DONE ***********************"
                );

                if (
                    CoffParser.parseCOFF(
                        functionname,
                        coff_data,
                        coff_data.Length,
                        arg_data,
                        arg_data.Length
                    ) == 1
                )
                {
                    Result = "ERROR";
                    beaconOutputData = "parseCOFF failed: 1";
                    beaconOutputData_sz = beaconOutputData.Length;
                }
                else
                {
                    beaconOutputData = CoffParser.getBeaconOutputData();
                    beaconOutputData_sz = beaconOutputData.Length;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(string.Format("Exception: '{0}'", e));
            }

            return Result;
        }

        public static int BeaconGetOutputData_Size()
        {
            return beaconOutputData_sz;
        }

        public static string BeaconGetOutputData()
        {
            return beaconOutputData;
        }

        public static string unhexlify(string hex)
        {
            string ret = null;
            for (int i = 0; i < hex.Length - 1; i += 2)
            {
                int value = Convert.ToInt32(hex.Substring(i, 2), 16);
                ret += char.ConvertFromUtf32(value);
            }
            return ret;
        }

        public static byte[] Decode(string encodedBuffer)
        {
            return Convert.FromBase64String(encodedBuffer);
        }
    }
}