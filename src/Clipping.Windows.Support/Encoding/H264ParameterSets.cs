namespace Clipping.Windows.Encoding;

// Retains codec headers independently of GOP retention and never combines sessions.
internal sealed class H264ParameterSets
{
    private byte[]? _sps;
    private byte[]? _pps;
    private byte[]? _header;

    // Both parameter sets are required for a playable MP4; return immutable snapshots for old clips.
    public byte[]? Update(byte[] data)
    {
        foreach (var (start, length) in NalUnits(data))
        {
            var type = data[start] & 0x1F;
            if (type is not (7 or 8))
            {
                continue;
            }

            var bytes = data.AsSpan(start, length).ToArray();
            if (type == 7)
            {
                _sps = bytes;
            }
            else
            {
                _pps = bytes;
            }

            _header = null;
        }

        if (_header is null && _sps is not null && _pps is not null)
        {
            _header = [0, 0, 0, 1, .. _sps, 0, 0, 0, 1, .. _pps];
        }

        return _header;
    }

    // Accept both three- and four-byte Annex B start codes from hardware output and media types.
    private static IEnumerable<(int Start, int Length)> NalUnits(byte[] data)
    {
        var start = -1;
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
            {
                continue;
            }

            if (start >= 0)
            {
                var end = i > 0 && data[i - 1] == 0 ? i - 1 : i;
                if (end > start)
                {
                    yield return (start, end - start);
                }
            }

            start = i + 3;
            i += 2;
        }

        if (start >= 0 && start < data.Length)
        {
            yield return (start, data.Length - start);
        }
    }
}
