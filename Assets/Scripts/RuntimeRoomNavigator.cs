using UnityEngine;

/// <summary>
/// Shared room-wide enemy flow-field for solid, top-down, 2D walls and stones.
/// Unlike single-ray obstacle steering, BFS routes enemies AROUND entire wall
/// clusters, including the new cross-shaped ricochet barrier.
///
/// Occupancy and navigation clearance are built from actual Collider2D shapes
/// after level geometry is generated. Actor colliders are excluded. All enemies
/// share the same cached field, avoiding 10 independent A* searches per tick.
/// </summary>
[DisallowMultipleComponent]
internal sealed class RuntimeRoomNavigator : MonoBehaviour
{
    private const float GridStep = 0.42f;
    private const float ClearanceRadius = 0.60f;
    private const float FlowRefreshInterval = 0.32f;

    private float originX;
    private float originY;
    private int columns;
    private int rows;
    private bool[,] walkable;
    private int[,] distance;
    private int[] queue;
    private int lastGoalX = -1;
    private int lastGoalY = -1;
    private float nextFlowRefresh;
    private bool hasFlow;

    private static readonly int[] OffsetX = { 1, -1, 0, 0 };
    private static readonly int[] OffsetY = { 0, 0, 1, -1 };

    public void Initialize(float halfWidth, float halfHeight)
    {
        float safeX = halfWidth - 0.98f;
        float safeY = halfHeight - 0.82f;
        columns = Mathf.FloorToInt(2f * safeX / GridStep) + 1;
        rows = Mathf.FloorToInt(2f * safeY / GridStep) + 1;
        columns = Mathf.Max(3, columns);
        rows = Mathf.Max(3, rows);
        originX = -GridStep * (columns - 1) * 0.5f;
        originY = -GridStep * (rows - 1) * 0.5f;
        walkable = new bool[columns, rows];
        distance = new int[columns, rows];
        queue = new int[columns * rows];

        Physics2D.SyncTransforms();
        int freeCount = 0;
        for (int x = 0; x < columns; x++)
        for (int y = 0; y < rows; y++)
        {
            Vector2 world = CellWorld(x, y);
            bool free = true;
            Collider2D[] hits = Physics2D.OverlapCircleAll(world, ClearanceRadius);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || !hit.enabled || hit.isTrigger)
                    continue;
                Rigidbody2D rb = hit.attachedRigidbody;
                if (rb == null || rb.bodyType == RigidbodyType2D.Static)
                {
                    free = false;
                    break;
                }
            }
            walkable[x, y] = free;
            if (free) freeCount++;
        }

        hasFlow = false;
        nextFlowRefresh = 0f;
        Debug.Log("Навигация комнаты: " + freeCount + "/" + (columns * rows) +
                  " свободных ячеек с радиусом " + ClearanceRadius.ToString("0.00"), this);
    }

    public bool TryGetDirection(Vector2 enemy, Vector2 goal, out Vector2 direction)
    {
        direction = Vector2.zero;
        if (walkable == null || columns <= 0 || rows <= 0)
            return false;

        // When there are no solid obstacles between the enemy and the player,
        // go straight to the player; that also makes close-range combat accurate.
        Vector2 toGoal = goal - enemy;
        if (toGoal.sqrMagnitude < 0.08f)
        {
            direction = toGoal.sqrMagnitude > 0.0001f ? toGoal.normalized : Vector2.zero;
            return true;
        }
        if (StraightPathClear(enemy, toGoal))
        {
            direction = toGoal.normalized;
            return true;
        }

        if (!FindNearestFree(goal, out int goalX, out int goalY, 5))
            return false;

        if (!hasFlow || goalX != lastGoalX || goalY != lastGoalY || Time.time >= nextFlowRefresh)
            RebuildFlow(goalX, goalY);

        if (!FindNearestFree(enemy, out int currentX, out int currentY, 4))
            return false;

        int currentDistance = distance[currentX, currentY];
        if (currentDistance < 0)
            return false;

        Vector2 currentCenter = CellWorld(currentX, currentY);
        Vector2 nextCenter = currentCenter;
        float bestCost = currentDistance * 10f;

        for (int i = 0; i < 4; i++)
        {
            int nx = currentX + OffsetX[i];
            int ny = currentY + OffsetY[i];
            if (nx < 0 || ny < 0 || nx >= columns || ny >= rows ||
                !walkable[nx, ny] || distance[nx, ny] < 0)
                continue;

            // The BFS gradient determines the route, while alignment breaks ties.
            float cost = distance[nx, ny] * 10f +
                Vector2.Distance(CellWorld(nx, ny), goal) * 0.035f;
            if (cost < bestCost - 0.0001f)
            {
                bestCost = cost;
                nextCenter = CellWorld(nx, ny);
            }
        }

        if ((nextCenter - currentCenter).sqrMagnitude < 0.001f)
        {
            // If the enemy reached the goal's cell, travel toward the exact player.
            if (currentDistance == 0)
            {
                direction = toGoal.normalized;
                return true;
            }
            return false;
        }

        Vector2 towardWaypoint = nextCenter - enemy;
        if (towardWaypoint.sqrMagnitude < 0.0001f)
            return false;

        direction = towardWaypoint.normalized;
        return true;
    }

    private bool StraightPathClear(Vector2 start, Vector2 delta)
    {
        float distanceToGoal = delta.magnitude;
        if (distanceToGoal < 0.01f) return true;
        RaycastHit2D[] hits = Physics2D.CircleCastAll(start, ClearanceRadius, delta / distanceToGoal,
                                                        distanceToGoal);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D hit = hits[i].collider;
            if (hit == null || !hit.enabled || hit.isTrigger)
                continue;
            Rigidbody2D rb = hit.attachedRigidbody;
            if (rb == null || rb.bodyType == RigidbodyType2D.Static)
                return false;
        }
        return true;
    }

    private void RebuildFlow(int targetX, int targetY)
    {
        lastGoalX = targetX;
        lastGoalY = targetY;
        nextFlowRefresh = Time.time + FlowRefreshInterval;
        hasFlow = true;
        for (int x = 0; x < columns; x++)
        for (int y = 0; y < rows; y++)
            distance[x, y] = -1;

        int head = 0;
        int tail = 0;
        distance[targetX, targetY] = 0;
        queue[tail++] = targetY * columns + targetX;
        while (head < tail)
        {
            int packed = queue[head++];
            int x = packed % columns;
            int y = packed / columns;
            int nextDistance = distance[x, y] + 1;
            for (int i = 0; i < 4; i++)
            {
                int nx = x + OffsetX[i];
                int ny = y + OffsetY[i];
                if (nx < 0 || ny < 0 || nx >= columns || ny >= rows ||
                    !walkable[nx, ny] || distance[nx, ny] >= 0)
                    continue;
                distance[nx, ny] = nextDistance;
                queue[tail++] = ny * columns + nx;
            }
        }
    }

    private bool FindNearestFree(Vector2 world, out int resultX, out int resultY, int searchRadius)
    {
        int approximateX = Mathf.RoundToInt((world.x - originX) / GridStep);
        int approximateY = Mathf.RoundToInt((world.y - originY) / GridStep);
        resultX = -1;
        resultY = -1;
        float best = float.PositiveInfinity;
        for (int y = Mathf.Max(0, approximateY - searchRadius);
             y <= Mathf.Min(rows - 1, approximateY + searchRadius); y++)
        for (int x = Mathf.Max(0, approximateX - searchRadius);
             x <= Mathf.Min(columns - 1, approximateX + searchRadius); x++)
        {
            if (!walkable[x, y]) continue;
            float sq = (CellWorld(x, y) - world).sqrMagnitude;
            if (sq < best)
            {
                best = sq;
                resultX = x;
                resultY = y;
            }
        }
        return resultX >= 0;
    }

    private Vector2 CellWorld(int x, int y)
    {
        return new Vector2(originX + x * GridStep, originY + y * GridStep);
    }
}
