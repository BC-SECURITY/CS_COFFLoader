using System;
using System.IO;
using System.Text;

namespace COFFLoader
{
    public class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.WriteLine("USAGE: coffloader.exe <functionName> <bof filename> <arguments>");
                Console.WriteLine("\tExample: coffloader.exe go whoami.o 00");
                return;
            }

            try
            {
                string functionName = args[0];
                byte[] rawCoff = File.ReadAllBytes(args[1]);
                string coffData = Convert.ToBase64String(rawCoff);
                string argDataHex = Convert.ToBase64String(Encoding.Default.GetBytes(args[2]));

                Console.WriteLine("Running COFFLoader...");
                string result = COFFLoader.RunCoff(functionName, coffData, argDataHex);

                Console.WriteLine($"Result: {result}");
                string output = COFFLoader.BeaconGetOutputData();
                int outputSize = COFFLoader.BeaconGetOutputData_Size();

                Console.WriteLine($"Output size: {outputSize}");
                Console.WriteLine("Output:");
                Console.WriteLine(output);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error occurred: {ex.Message}");
            }
        }
    }
}