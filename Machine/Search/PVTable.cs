using Machine.Core;

namespace Machine.Search;

/// <summary>
/// Principal Variation table for collecting PV during search
/// Uses triangular array structure for efficient PV collection
/// </summary>
public sealed class PVTable
{
    private const int MaxPly = 128;
    private readonly Move[,] _pvArray = new Move[MaxPly, MaxPly];
    private readonly int[] _pvLength = new int[MaxPly];
    
    public void Clear()
    {
        for (int i = 0; i < MaxPly; i++)
        {
            _pvLength[i] = 0;
            for (int j = 0; j < MaxPly; j++)
                _pvArray[i, j] = Move.NullMove;
        }
    }
    
    /// <summary>
    /// Update PV when a new best move is found
    /// Copies the PV from ply+1 and prepends the current move
    /// </summary>
    public void UpdatePV(int ply, Move move)
    {
        _pvArray[ply, ply] = move;
        
        // Copy PV from next ply
        for (int i = ply + 1; i < ply + 1 + _pvLength[ply + 1]; i++)
        {
            _pvArray[ply, i] = _pvArray[ply + 1, i];
        }
        
        _pvLength[ply] = _pvLength[ply + 1] + 1;
    }
    
    /// <summary>
    /// Clear PV length at the start of a node
    /// </summary>
    public void ClearPly(int ply)
    {
        _pvLength[ply] = 0;
    }
    
    /// <summary>
    /// Get the principal variation from root
    /// </summary>
    public Move[] GetPV()
    {
        var pv = new Move[_pvLength[0]];
        for (int i = 0; i < _pvLength[0]; i++)
            pv[i] = _pvArray[0, i];
        return pv;
    }
    
    /// <summary>
    /// Get the best move from the root position
    /// </summary>
    public Move GetBestMove()
    {
        return _pvLength[0] > 0 ? _pvArray[0, 0] : Move.NullMove;
    }
}