using System;

class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length==1 && args[0]=="selfcheck") { StockCylinder.Selfcheck(); return 0; }
            throw new ArgumentException("selfcheck or replay RECORDING OUTPUT required.");
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
