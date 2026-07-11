using DiskWeaver.Cli;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: diskweaver <command> [options]");
    Console.Error.WriteLine("Commands: inventory, plan, wipe, testkit");
    return 1;
}

return args[0] switch
{
    "inventory" => InventoryCommand.Run(args[1..]),
    "plan" => PlanCommand.Run(args[1..]),
    "wipe" => WipeCommand.Run(args[1..]),
    "testkit" => TestKitCommand.Run(args[1..]),
    _ => Fail($"Unknown command '{args[0]}'."),
};

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
