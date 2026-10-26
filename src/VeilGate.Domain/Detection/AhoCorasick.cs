namespace VeilGate.Domain.Detection;

public sealed class AhoCorasick
{
    private sealed class Node
    {
        public readonly Dictionary<char, Node> Next = new();
        public Node? Fail;
        public readonly List<int> Outputs = new();   // lengths of words that end at this node
    }

    private readonly Node _root = new();
    private readonly bool _caseSensitive;

    public AhoCorasick(IEnumerable<string> terms, bool caseSensitive = false)
    {
        _caseSensitive = caseSensitive;

        // 1. Build the tree: one path of nodes per word, shared where words start the same.
        foreach (var raw in terms)
        {
            var term = Prepare(raw.Trim());
            if (term.Length == 0) continue;
            var node = _root;
            foreach (var ch in term)
            {
                if (!node.Next.TryGetValue(ch, out var child)) node.Next[ch] = child = new Node();
                node = child;
            }
            node.Outputs.Add(term.Length);
        }

        // 2. Add "fail links": where to jump when the next character doesn't continue the current path.
        var queue = new Queue<Node>();
        foreach (var child in _root.Next.Values) { child.Fail = _root; queue.Enqueue(child); }
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var (ch, child) in node.Next)
            {
                var f = node.Fail!;
                while (f != _root && !f.Next.ContainsKey(ch)) f = f.Fail!;
                child.Fail = f.Next.TryGetValue(ch, out var target) ? target : _root;
                child.Outputs.AddRange(child.Fail.Outputs);   // a word that ends inside this one also matches here
                queue.Enqueue(child);
            }
        }
    }

    private string Prepare(string s) => _caseSensitive ? s : s.ToLowerInvariant();

    public IEnumerable<(int Start, int End)> Find(string text)
    {
        var haystack = Prepare(text);
        var node = _root;
        for (var i = 0; i < haystack.Length; i++)
        {
            while (node != _root && !node.Next.ContainsKey(haystack[i])) node = node.Fail!;
            if (node.Next.TryGetValue(haystack[i], out var next)) node = next;

            foreach (var len in node.Outputs)
            {
                var start = i + 1 - len;
                var end = i + 1;
                // Whole words only: "Ann" must not match inside "Annual".
                var leftOk = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
                var rightOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
                if (leftOk && rightOk) yield return (start, end);
            }
        }
    }
}