#if DEBUG
using copilot_auto_byok.Services;

namespace copilot_auto_byok;

public static class TestBooleanConversion
{
    public static void RunTests()
    {
        Console.WriteLine("Testing Python-style boolean conversion...");

        // Test cases - note: JsonSerializer.Serialize removes spaces by default
        var testCases = new (string input, string expected)[]
        {
            // Simple cases (fragment, not full JSON - won't parse, return as-is)
            (": True", ": true"),
            (":True", ":true"),
            (": False", ": false"),
            (":False", ":false"),
            
            // In arrays (fragments)
            ("[True", "[true"),
            ("[False", "[false"),
            
            // In objects (fragments)
            (", True", ", true"),
            (",True", ",true"),
            (", False", ", false"),
            (",False", ",false"),
            
            // Valid JSON objects (will be re-serialized without spaces)
            ("{\"key\": True}", "{\"key\":true}"),
            ("{\"key\": False}", "{\"key\":false}"),
            ("{\"a\": True, \"b\": False}", "{\"a\":true,\"b\":false}"),
            
            // JSON array
            ("[True, False, True]", "[true,false,true]"),
            
            // Nested objects
            ("{\"nested\": {\"flag\": True}}", "{\"nested\":{\"flag\":true}}"),
            
            // String values should NOT be converted
            ("{\"key\": \"True\"}", "{\"key\":\"True\"}"),
            ("{\"key\": \"False\"}", "{\"key\":\"False\"}"),
            
            // Standalone True (valid JSON)
            ("True", "true"),
            
            // Multiple spaces (fragments)
            (":   True", ":   true"),
            (":   False", ":   false"),
            
            // Tool call format fragments
            ("\"isRegexp\": True", "\"isRegexp\": true"),
            ("\"isRegexp\":True", "\"isRegexp\":true"),
            ("\"isRegexp\": False", "\"isRegexp\": false"),
            ("\"isRegexp\":False", "\"isRegexp\":false"),
            
            // Valid JSON with tool parameters
            ("{\"isRegexp\": True, \"maxResults\": 10}", "{\"isRegexp\":true,\"maxResults\":10}"),
        };

        int passed = 0;
        int failed = 0;

        foreach (var (input, expected) in testCases)
        {
            var result = BooleanConvertStream.ConvertPythonBooleans(input);
            if (result == expected)
            {
                Console.WriteLine($"✓ PASS: \"{input}\" → \"{result}\"");
                passed++;
            }
            else
            {
                Console.WriteLine($"✗ FAIL: \"{input}\" → \"{result}\" (expected: \"{expected}\")");
                failed++;
            }
        }

        Console.WriteLine($"\nResults: {passed} passed, {failed} failed, {passed + failed} total");
    }
}
#endif