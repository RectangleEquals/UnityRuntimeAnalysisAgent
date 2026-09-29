namespace Broken;

public class Fine
{
    public int value = 1;
}

public class DerivesFromMissing : Missing.MissingBase
{
}

public class UsesMissing
{
    public object Make() => new Missing.MissingBase();
}
