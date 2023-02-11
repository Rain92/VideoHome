namespace VideoHome.Services;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;

public class CounterService
{
    private int _globalCounter;
    public int GlobalCounter => _globalCounter;

    private const string COUNTER_FILENAME = "counterstate.txt";
    private IWebHostEnvironment _environment;

    public CounterService(IWebHostEnvironment environment)
    {
        _environment = environment;
        LoadCounter();
    }

    public void IncrementCounter()
    {
        Interlocked.Increment(ref _globalCounter);
        SaveCounter();
    }

    public void LoadCounter()
    {
        var path = Path.Join(_environment.WebRootPath, COUNTER_FILENAME);
        try
        {
            var str = File.ReadAllText(path);
            _globalCounter = int.Parse(str);
        }
        catch
        {
            _globalCounter = 0;
        }
    }

    public void SaveCounter()
    {
        var path = Path.Join(_environment.WebRootPath, COUNTER_FILENAME);
        File.WriteAllText(path, GlobalCounter.ToString());
    }
}