<script lang="ts">
  import * as d3 from 'd3'
  import { api, ApiError } from './api'

  let { onclose }: { onclose: () => void } = $props()

  type ApiNode = { id: string; name: string; isMe: boolean; tier: string; degree: number }
  type ApiEdge = { from: string; to: string; cluster: number }

  // d3 mutates node positions, so the simulation works on its own copies.
  type SimNode = d3.SimulationNodeDatum & { id: string; name: string; isMe: boolean; tier: string; degree: number }
  type SimLink = d3.SimulationLinkDatum<SimNode> & { cluster: number }

  let svgElement: SVGSVGElement | undefined = $state()
  let loading = $state(true)
  let error = $state<string | null>(null)
  let contacts = $state(0)
  let friendships = $state(0)
  let empty = $state(false)

  // Held until the <svg> exists. The element lives inside the !empty branch, so it is not
  // mounted while data is still loading - calling render() straight from load() silently
  // drew nothing because svgElement was still undefined.
  let pending: { nodes: ApiNode[]; edges: ApiEdge[] } | null = $state(null)

  let simulation: d3.Simulation<SimNode, SimLink> | undefined
  let zoom: d3.ZoomBehavior<SVGSVGElement, unknown> | undefined

  // Spread across the hue wheel rather than a violet ramp. Neighbouring tiers were
  // indistinguishable at node size (best / close / friend were all lavender), which defeats
  // the point of colouring them.
  const tierColour: Record<string, string> = {
    me: '#22d3ee',
    family: '#f472b6',
    'extended-family': '#fb923c',
    best: '#e879f9',
    close: '#a78bfa',
    friend: '#60a5fa',
    acquaintance: '#34d399',
    unknown: '#94a3b8',
  }

  /**
   * Draws the network with a d3 force simulation.
   *
   * The previous version placed nodes on fixed rings in a 100-unit viewBox and drew labels
   * at a constant offset above each circle. That offset was expressed in the same scaled
   * units as everything else, so it looked fine at one zoom level and wrong at every other;
   * and nothing could be moved, so a hairball stayed a hairball.
   *
   * This follows the established d3 pattern: forceCentre to pin "you", forceLink to keep
   * friends near their connections, forceManyBody to push unrelated nodes apart, and
   * forceCollide so circles never overlap. d3.drag lets a node be repositioned and pinned;
   * d3.zoom scales the whole group, so labels keep their relation to their circles.
   */
  function render(nodes: ApiNode[], edges: ApiEdge[]) {
    if (!svgElement) {
      return
    }

    simulation?.stop()

    const svg = d3.select(svgElement)
    svg.selectAll('*').remove()

    const width = svgElement.clientWidth || 900
    const height = svgElement.clientHeight || 600

    const simNodes: SimNode[] = nodes.map((n) => ({
      id: n.id,
      name: n.name,
      isMe: n.isMe,
      tier: n.tier,
      degree: n.degree,
    }))

    const byId = new Map(simNodes.map((n) => [n.id, n]))

    const simLinks: SimLink[] = edges
      .map((e) => ({ source: byId.get(e.from), target: byId.get(e.to), cluster: e.cluster }))
      .filter((l) => l.source && l.target) as SimLink[]

    // Everything lives inside one group so zoom and pan move labels with their circles.
    const root = svg.append('g')

    const link = root
      .append('g')
      .selectAll('line')
      .data(simLinks)
      .join('line')
      .attr('stroke', (d) => (d.cluster === 1 ? 'rgba(34,211,238,0.18)' : 'rgba(34,211,238,0.55)'))
      .attr('stroke-width', (d) => (d.cluster === 1 ? 1 : 1.8))

    const node = root
      .append('g')
      .selectAll('g')
      .data(simNodes)
      .join('g')
      .attr('class', 'gnode')

    node
      .append('circle')
      .attr('r', (d) => (d.isMe ? 14 : Math.min(7 + d.degree * 1.6, 13)))
      .attr('fill', (d) => tierColour[d.tier] ?? '#94a3b8')
      .attr('stroke', (d) => (d.isMe ? '#67e8f9' : 'rgba(255,255,255,0.35)'))
      .attr('stroke-width', (d) => (d.isMe ? 2.5 : 1.2))
      .style('cursor', 'grab')

    // The label sits just under the circle and moves with it: it is part of the node group,
    // not a separate text element positioned by hand.
    node
      .append('text')
      .text((d) => (d.isMe ? 'You' : d.name))
      .attr('text-anchor', 'middle')
      .attr('y', (d) => (d.isMe ? 26 : 20))
      .attr('font-size', (d) => (d.isMe ? 13 : 11))
      .attr('font-family', 'inherit')
      .attr('fill', (d) => (d.isMe ? '#67e8f9' : 'rgba(228,236,255,0.82)'))
      .attr('pointer-events', 'none')

    node.append('title').text((d) => d.name)

    const simulationRef = d3
      .forceSimulation(simNodes)
      // "You" stays in the middle: the whole picture is your relationships.
      .force('center', d3.forceCenter(width / 2, height / 2))
      .force('charge', d3.forceManyBody().strength(-420))
      .force('link', d3.forceLink(simLinks).distance((d) => (d.cluster === 1 ? 150 : 90)).strength(0.35))
      // Collision keeps circles apart so labels have room.
      .force('collide', d3.forceCollide<SimNode>().radius((d) => (d.isMe ? 26 : 22)))

    const me = simNodes.find((n) => n.isMe)

    if (me) {
      me.fx = width / 2
      me.fy = height / 2
    }

    simulation = simulationRef

    node.call(
      d3
        .drag<SVGGElement, SimNode>()
        .on('start', (event, d) => {
          if (!event.active) simulationRef.alphaTarget(0.3).restart()
          d.fx = d.x
          d.fy = d.y
        })
        .on('drag', (event, d) => {
          // "You" is pinned to the centre by design, so it does not move.
          if (d.isMe) return
          d.fx = event.x
          d.fy = event.y
        })
        .on('end', (event, d) => {
          if (!event.active) simulationRef.alphaTarget(0)
          // Leaving fx/fy set pins the node where it was dropped, which is what a reader
          // expects after deliberately moving something. Double-click releases it.
          if (d.isMe) return
        }),
    )

    node.on('dblclick', (event, d) => {
      event.preventDefault()
      if (d.isMe) return
      d.fx = null
      d.fy = null
      simulationRef.alphaTarget(0.2).restart()
    })

    simulationRef.on('tick', () => {
      link
        .attr('x1', (d) => (d.source as SimNode).x!)
        .attr('y1', (d) => (d.source as SimNode).y!)
        .attr('x2', (d) => (d.target as SimNode).x!)
        .attr('y2', (d) => (d.target as SimNode).y!)

      node.attr('transform', (d) => `translate(${d.x},${d.y})`)
    })

    const zoomRef = d3
      .zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.25, 4])
      .on('zoom', (event) => {
        root.attr('transform', event.transform)
      })

    zoom = zoomRef
    svg.call(zoomRef)

    // A visible hint that the graph is pannable, since an SVG does not look draggable.
    svg.style('cursor', 'grab')
  }

  async function load() {
    loading = true
    error = null

    try {
      const result = await api.graph()
      contacts = result.contacts
      friendships = result.friendships
      empty = result.contacts === 0
      pending = { nodes: result.nodes as ApiNode[], edges: result.edges as ApiEdge[] }
    } catch (e) {
      error = e instanceof ApiError ? e.message : String(e)
    } finally {
      loading = false
    }
  }

  function reset() {
    if (simulation && svgElement && zoom) {
      d3.select(svgElement).transition().duration(400).call(zoom.transform, d3.zoomIdentity)
      simulation.alpha(0.8).restart()
    }
  }

  // Draws whenever both the data and the element are available.
  $effect(() => {
    if (pending && svgElement) {
      render(pending.nodes, pending.edges)
    }
  })

  $effect(() => {
    load()

    return () => simulation?.stop()
  })
</script>

<div class="graph-view">
  <header class="party-head">
    <h1>Network</h1>
    <div class="party-head-actions">
      <button onclick={reset}>Reset view</button>
      <button onclick={load}>Relayout</button>
      <button onclick={onclose}>Back to contacts</button>
    </div>
  </header>

  {#if error}
    <p class="note-error" role="alert">{error}</p>
  {/if}

  {#if loading}
    <p class="muted">Loading the network…</p>
  {:else if empty}
    <div class="graph-empty">
      <p>No friendships recorded yet.</p>
      <p class="muted">
        Open a contact and use <strong>Friends with</strong> to link people who know each other.
      </p>
    </div>
  {:else}
    <p class="graph-summary">
      {contacts} people · {friendships} friendships · drag a node to move it,
      drag the background to pan, scroll to zoom
    </p>

    <svg
      class="graph-svg"
      bind:this={svgElement}
      role="img"
      aria-label="Friend network: you at the centre with {contacts} contacts"
    ></svg>
  {/if}
</div>
