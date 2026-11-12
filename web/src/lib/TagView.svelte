<script lang="ts">
  import * as d3 from 'd3'
  import { api, ApiError } from './api'
  import { tierHue } from './format'

  let {
    onselect = () => {},
  }: {
    onselect?: (id: string) => void
  } = $props()

  type Group = { tag: string; count: number; members: { id: string; name: string; tier: string }[] }

  let svgElement: SVGSVGElement | undefined = $state()
  let loading = $state(true)
  let error = $state<string | null>(null)
  let groups = $state<Group[]>([])
  let taggedPeople = $state(0)
  let tagCount = $state(0)

  // The most members drawn per tag before summarising. A tag with 178 people would otherwise
  // be a solid ring of overlapping dots with no room for any other tag on screen.
  const MAX_MEMBERS = 12

  /** Space reserved outside each bubble for the member dots and their names. */
  const ORBIT_RESERVE = 30

  /** Widest a member name may be drawn before being clipped. */
  const MAX_NAME_CHARS = 13

  /**
   * Colours sampled from a wide range, ordered by popularity.
   *
   * Sizing already encodes frequency, so colour is free to encode something else useful:
   * which cluster is which, at a glance, when tags interleave.
   */
  const palette = [
    '#22d3ee',
    '#a78bfa',
    '#f472b6',
    '#34d399',
    '#fbbf24',
    '#60a5fa',
    '#fb923c',
    '#e879f9',
    '#4ade80',
    '#f87171',
    '#38bdf8',
    '#c084fc',
  ]

  /**
   * Lays the tags out as a bubble field rather than a force simulation.
   *
   * A force layout was wrong here: the tags are not connected by anything, so the only forces
   * would be repulsion and gravity, which produces an arrangement that differs on every load
   * and means nothing. Placing the largest tag first and packing the rest into the remaining
   * space makes size comparable, which is the entire point of the view.
   */
  /**
   * Packs circles into a rectangle without letting any escape it.
   *
   * The previous version spiralled outward until a position was free, and after 4000 attempts
   * gave up and *kept* whatever position it had reached - which is off the canvas. That is why
   * most tags were invisible: the packer was silently failing and drawing them outside the
   * frame. Escaping the rectangle is now impossible by construction.
   *
   * Placement is a bounded spiral restricted to the rectangle; if the spiral is exhausted the
   * circle is placed at the point with the most clearance, so overlapping is preferred to
   * disappearing.
   */
  function pack(
    items: { index: number; radius: number; outer: number }[],
    width: number,
    height: number,
  ) {
    const placed: { index: number; x: number; y: number; radius: number; outer: number }[] = []

    const clamp = (item: { outer: number }, x: number, y: number) => ({
      // An item larger than the box cannot be clamped; centre it and let it clip.
      x: Math.min(Math.max(x, item.outer + 4), Math.max(item.outer + 4, width - item.outer - 4)),
      y: Math.min(Math.max(y, item.outer + 4), Math.max(item.outer + 4, height - item.outer - 4)),
    })

    const clearanceAt = (item: { outer: number }, x: number, y: number) => {
      let best = Infinity

      for (const p of placed) {
        const dx = p.x - x
        const dy = p.y - y
        best = Math.min(best, Math.sqrt(dx * dx + dy * dy) - p.outer - item.outer)
      }

      return best
    }

    let angle = 0
    let distance = 0

    for (const item of items) {
      let chosen: { x: number; y: number } | null = null

      // The spiral is bounded by the canvas diagonal, so it cannot walk off forever.
      const maxDistance = Math.sqrt(width * width + height * height)

      angle = 0
      distance = 0

      while (distance <= maxDistance) {
        const raw = clamp(
          item,
          width / 2 + Math.cos(angle) * distance,
          height / 2 + Math.sin(angle) * distance * 0.85,
        )

        if (clearanceAt(item, raw.x, raw.y) >= 6) {
          chosen = raw
          break
        }

        angle += 0.35
        distance += 2.5
      }

      if (!chosen) {
        // Nothing free: take the roomiest spot inside the box rather than leaving the frame.
        let best = { x: width / 2, y: height / 2 }
        let bestClearance = -Infinity

        for (let gx = item.outer + 4; gx <= width - item.outer - 4; gx += Math.max(12, item.outer / 2)) {
          for (let gy = item.outer + 4; gy <= height - item.outer - 4; gy += Math.max(12, item.outer / 2)) {
            const clearance = clearanceAt(item, gx, gy)

            if (clearance > bestClearance) {
              bestClearance = clearance
              best = { x: gx, y: gy }
            }
          }
        }

        chosen = best
      }

      placed.push({ index: item.index, x: chosen.x, y: chosen.y, radius: item.radius, outer: item.outer })
    }

    return placed
  }

  function render() {
    if (!svgElement || groups.length === 0) {
      return
    }

    const svg = d3.select(svgElement)
    svg.selectAll('*').remove()

    const width = svgElement.clientWidth || 900
    const height = svgElement.clientHeight || 560

    const largest = Math.max(...groups.map((g) => g.count))

    // Area, not radius, encodes the count: a tag with twice the people gets twice the ink.
    // Scaling the radius directly made the biggest tag look four times the size of a tag with
    // a quarter of the people.
    // Scale the whole field to the canvas. A fixed maximum looked right with a dozen tags and
    // made most of them invisible with two dozen, because the packer ran out of room and had
    // nowhere legal left to put them.
    //
    // Total ink is bounded by the area, then each bubble takes a share of it proportional to
    // the square root of its count - area, not radius, so a tag with four times the people is
    // four times the size rather than sixteen.
    // 0.30 rather than 0.42 of the canvas: the packer needs slack around each circle to find
    // a legal spot, and at 0.42 most tags fell through to the overlap fallback.
    const area = width * height * 0.30
    const totalWeight = groups.reduce((sum, g) => sum + Math.sqrt(g.count), 0)
    const unit = Math.sqrt(area / Math.max(totalWeight, 1))

    const radiusFor = (count: number) => {
      // A floor keeps single-person tags clickable; the cap stops huge tags becoming the
      // whole picture while tiny ones vanish.
      const raw = Math.sqrt(count) * unit * 0.5

      return Math.max(22, Math.min(raw, Math.min(width, height) * 0.2))
    }

    // Room outside the bubble for the orbit ring and the member labels.
    //
    // A fixed 26px pad was too small: the dot sits 12px out, but its name is drawn beside it,
    // so names hung off the right edge. Reserving the *full* width of the longest name is the
    // other extreme - it wants ~130px on all four sides of every bubble, which cannot fit 22
    // tags on one canvas. So the reserve covers a typical label and the labels themselves are
    // clipped to what was reserved. Names are available in full on hover (the <title>) and by
    // clicking through, which is where they matter.
    const outerFor = (group: Group) => radiusFor(group.count) + ORBIT_RESERVE

    const placed = pack(
      groups.map((g, index) => ({
        index,
        radius: radiusFor(g.count),
        outer: outerFor(g),
      })),
      width,
      height,
    )

    const root = svg.append('g')

    const bubble = root
      .append('g')
      .selectAll('g')
      .data(placed)
      .join('g')
      .attr('transform', (d) => `translate(${d.x},${d.y})`)
      .style('cursor', 'pointer')
      .on('click', (_event, d) => {
        const group = groups[d.index]
        const first = group.members[0]

        if (first) {
          onselect(first.id)
        }
      })

    bubble
      .append('circle')
      .attr('r', (d) => d.radius)
      .attr('fill', (d) => palette[d.index % palette.length])
      .attr('fill-opacity', 0.16)
      .attr('stroke', (d) => palette[d.index % palette.length])
      .attr('stroke-opacity', 0.75)
      .attr('stroke-width', 1.5)

    // The tag name, above the count. Truncated rather than wrapped: a bubble is a fixed
    // shape, and text spilling outside it reads as a different tag's label.
    bubble
      .append('text')
      .attr('text-anchor', 'middle')
      .attr('y', (d) => (d.radius > 46 ? -2 : 0))
      .attr('font-size', (d) => Math.max(11, Math.min(16, d.radius * 0.32)))
      .attr('font-weight', 600)
      .attr('fill', 'rgba(20, 28, 44, 0.92)')
      .attr('pointer-events', 'none')
      .text((d) => {
        const label = groups[d.index].tag
        const fontSize = Math.max(11, Math.min(16, d.radius * 0.32))

        // The budget has to be measured in the em of the font actually drawn. Dividing the
        // radius by a constant ignored the fact that the font size scales with the radius
        // too, so the two cancelled out and a 120px bubble was given no more characters
        // than a 26px one - which is why "double-degr…" was clipped inside a huge circle.
        //
        // 0.56em is a reasonable average advance width for the display face at these sizes;
        // the usable width is the chord across the bubble at the text's baseline.
        const usable = d.radius * 1.5
        const perChar = fontSize * 0.56
        const budget = Math.max(3, Math.floor(usable / perChar))

        return label.length > budget ? `${label.slice(0, budget - 1)}…` : label
      })

    // The count, which is what the size means.
    bubble
      .append('text')
      .attr('text-anchor', 'middle')
      .attr('y', (d) => (d.radius > 46 ? 16 : 14))
      .attr('font-size', (d) => Math.max(10, Math.min(13, d.radius * 0.26)))
      .attr('fill', 'rgba(40, 52, 72, 0.75)')
      .attr('pointer-events', 'none')
      .text((d) => String(groups[d.index].count))

    // Members orbit the tag: a small dot per person, tinted by relationship tier, so the view
    // shows who is filed where and not only how many.
    const orbit = root
      .append('g')
      .selectAll('g')
      .data(
        placed.flatMap((p) => {
          const group = groups[p.index]
          const shown = group.members.slice(0, MAX_MEMBERS)
          const ringRadius = p.radius + 12

          return shown.map((member, i) => {
            const angle = (2 * Math.PI * i) / Math.max(shown.length, 1) - Math.PI / 2

            return {
              ...member,
              tag: group.tag,
              x: p.x + Math.cos(angle) * ringRadius,
              y: p.y + Math.sin(angle) * ringRadius,
            }
          })
        }),
      )
      .join('g')
      .attr('transform', (d) => `translate(${d.x},${d.y})`)
      .style('cursor', 'pointer')
      .on('click', (_event, d) => onselect(d.id))

    orbit.append('circle').attr('r', 4.5).attr('fill', (d) => `hsl(${tierHue(d.tier)} 62% 52%)`)

    orbit.append('title').text((d) => `${d.name} — ${d.tag}`)

    // Only the first few members are named; naming every dot would bury the bubbles.
    orbit
      .append('text')
      .text((d) =>
        d.name.length > MAX_NAME_CHARS ? `${d.name.slice(0, MAX_NAME_CHARS - 1)}…` : d.name,
      )
      .attr('text-anchor', (d) => (Math.cos(Math.atan2(d.y - height / 2, d.x - width / 2)) > 0 ? 'start' : 'end'))
      .attr('dx', (d) => (Math.cos(Math.atan2(d.y - height / 2, d.x - width / 2)) > 0 ? 7 : -7))
      .attr('dy', 3.5)
      .attr('font-size', 10)
      .attr('fill', 'rgba(60, 72, 94, 0.85)')
      .attr('pointer-events', 'none')
  }

  async function load() {
    loading = true
    error = null

    try {
      const result = await api.tagGraph()
      groups = result.groups
      taggedPeople = result.taggedPeople
      tagCount = result.tagCount
    } catch (e) {
      error = e instanceof ApiError ? e.message : String(e)
    } finally {
      loading = false
    }
  }

  $effect(() => {
    load()
  })

  // Redraw whenever the data or the element changes. The element only exists once data has
  // arrived, so both have to be watched - rendering from load() alone drew into nothing.
  $effect(() => {
    if (svgElement && groups.length > 0) {
      render()
    }
  })
</script>

{#if error}
  <p class="note-error" role="alert">{error}</p>
{/if}

{#if loading}
  <p class="muted">Loading tags…</p>
{:else if groups.length === 0}
  <div class="graph-empty">
    <p>No tags recorded yet.</p>
    <p class="muted">Open a contact and add a tag to start grouping them.</p>
  </div>
{:else}
  <p class="graph-summary">
    {tagCount} tags · {taggedPeople} people tagged · bubble size is how many people carry the
    tag · click a bubble or a name to open a contact
  </p>

  <svg
    class="graph-svg"
    bind:this={svgElement}
    role="img"
    aria-label="Tag landscape: {tagCount} tags across {taggedPeople} people, sized by how many carry each"
  ></svg>
{/if}
