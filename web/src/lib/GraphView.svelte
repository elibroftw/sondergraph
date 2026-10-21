<script lang="ts">
  import { api, ApiError } from './api'
  import { platformLabel } from './format'

  let { onclose }: { onclose: () => void } = $props()

  type Node = { id: string; name: string; isMe: boolean; tier: string; x: number; y: number; degree: number }
  type Edge = { from: string; to: string; cluster: number }

  let nodes = $state<Node[]>([])
  let edges = $state<Edge[]>([])
  let contacts = $state(0)
  let friendships = $state(0)
  let loading = $state(true)
  let error = $state<string | null>(null)
  let hovered = $state<string | null>(null)

  async function load() {
    loading = true
    error = null

    try {
      const result = await api.graph()
      nodes = result.nodes
      edges = result.edges
      contacts = result.contacts
      friendships = result.friendships
    } catch (e) {
      error = e instanceof ApiError ? e.message : String(e)
    } finally {
      loading = false
    }
  }

  const positions = $derived.by(() => {
    const map: Record<string, { x: number; y: number }> = {}
    for (const node of nodes) {
      // The server caps the layout radius at 0.94, so scaling by 40 puts the outermost
      // node at 50 +/- 38 and leaves margin for its circle and label. Scaling by 88 was a
      // wrong fix: it looked like the graph was "too clustered", but the real problem was
      // tiny node radii, and 88 pushed the outer ring past the canvas edge entirely.
      map[node.id] = { x: 50 + node.x * 40, y: 50 + node.y * 40 }
    }
    return map
  })

  const tierHue: Record<string, number> = {
    me: 190,
    family: 320,
    'extended-family': 340,
    best: 300,
    close: 270,
    friend: 205,
    acquaintance: 160,
    unknown: 40,
  }

  $effect(() => {
    load()
  })
</script>

<div class="graph-view">
  <header class="party-head">
    <h1>Network</h1>
    <div class="party-head-actions">
      <button onclick={load}>Refresh</button>
      <button onclick={onclose}>Back to contacts</button>
    </div>
  </header>

  {#if error}
    <p class="note-error" role="alert">{error}</p>
  {/if}

  {#if loading}
    <p class="muted">Loading the network…</p>
  {:else if contacts === 0}
    <div class="graph-empty">
      <p>No friendships recorded yet.</p>
      <p class="muted">
        Open a contact and use <strong>Friends with</strong> to link people who know each other.
        Once a few are linked, this view shows who is connected to whom.
      </p>
    </div>
  {:else}
    <p class="graph-summary">
      {contacts} people in the network · {friendships} friendships between them
    </p>

    <svg class="graph-svg" viewBox="0 0 100 100" preserveAspectRatio="xMidYMid meet" role="img"
         aria-label="Friend network: you at the centre with {contacts} connected contacts">
      <!-- Contact-to-contact edges first, so the spokes draw over them. -->
      {#each edges.filter((e) => e.cluster === 2) as edge (edge.from + edge.to)}
        {#if positions[edge.from] && positions[edge.to]}
          <line
            x1={positions[edge.from].x} y1={positions[edge.from].y}
            x2={positions[edge.to].x} y2={positions[edge.to].y}
            class="graph-edge"
            class:highlight={hovered === edge.from || hovered === edge.to}
          />
        {/if}
      {/each}

      {#each edges.filter((e) => e.cluster === 1) as edge (edge.from + edge.to)}
        {#if positions[edge.to]}
          <line
            x1={positions.me.x} y1={positions.me.y}
            x2={positions[edge.to].x} y2={positions[edge.to].y}
            class="graph-spoke"
          />
        {/if}
      {/each}

      {#each nodes as node (node.id)}
        <g
          class="graph-node"
          onmouseenter={() => (hovered = node.id)}
          onmouseleave={() => (hovered = null)}
        >
          <circle
            cx={positions[node.id].x}
            cy={positions[node.id].y}
            r={node.isMe ? 4.5 : Math.min(2.2 + node.degree * 0.5, 5)}
            fill="hsl({tierHue[node.tier] ?? 200} 75% {node.isMe ? 65 : 58}%)"
            class:center={node.isMe}
          />
          {#if node.isMe || node.degree > 0 || hovered === node.id}
            <text x={positions[node.id].x} y={positions[node.id].y - 6} class="graph-label">
              {node.isMe ? 'You' : node.name}
            </text>
          {/if}
        </g>
      {/each}
    </svg>

    <p class="graph-legend muted">
      Ring position is relationship closeness: family and best friends nearest the centre,
      unrated outermost. Node size is how many friendships that person has here.
    </p>
  {/if}
</div>
