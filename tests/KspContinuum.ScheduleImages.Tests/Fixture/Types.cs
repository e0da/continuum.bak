namespace ScheduleImageFixture
{
    public static class Witness { public static int Constructors; }
    public sealed class First { public First(){Witness.Constructors++;} }
    public sealed class Second { public Second(){Witness.Constructors++;} }
}
