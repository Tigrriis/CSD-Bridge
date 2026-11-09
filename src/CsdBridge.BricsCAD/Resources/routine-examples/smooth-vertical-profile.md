---
name: Smooth vertical profile
description: Propose a smoother vertical for a CSD road with fewer IPs, within grade, K and spacing limits. Doesn't change the design.
effort: thorough
inputs:
  - name: road
    label: Road
    type: text
    required: true
    help: The CSD road name, e.g. MC01
  - name: surface
    label: Existing surface
    type: text
    default: NS
    help: The CSD surface for existing ground
  - name: max_grade
    label: Max grade
    type: number
    default: 8
    unit: "%"
  - name: min_grade
    label: Min grade
    type: number
    default: 0.5
    unit: "%"
  - name: k_value
    label: K value
    type: number
    default: 10
  - name: min_ip_spacing
    label: Min IP spacing
    type: number
    default: 40
    unit: m
  - name: fixed_levels
    label: Levels to hold
    type: text
    help: e.g. ch 52.581 at the existing level; leave blank if none
---

This is an example routine: edit it, or delete it once you have your own.

1. Open the Vertical Grading Editor for {{road}} with csd_open_vge. If it asks for the road to be picked, ask the user to pick it. Note the current IP table and the volumes.
2. Find {{road}}'s centreline and its start chainage (drawing notes first, then the alignment layers). Sample the existing ground along it from the {{surface}} surface with sample_csd_ground.
3. Design a new vertical with design_vertical_profile: max grade {{max_grade}} %, min grade {{min_grade}} %, K {{k_value}}, IPs at least {{min_ip_spacing}} m apart, and the current IPs for comparison. Hold these levels as fixed IPs, if any: {{fixed_levels}}.
4. Check the proposal with evaluate_vertical_profile and fix any grade, K, spacing or VC overlap problems.
5. Write the proposed IP table with csd_write_vc_file, named after {{road}}.
6. Save a drawing note with the road, its centreline handle and start chainage, if they aren't noted already.

Rules:
- Don't change the design in CSD. The user imports the VC file themselves (VGE: File > Import VC).

Before finishing, report in a short table: current vs proposed number of IPs and centreline cut/fill, any limit that couldn't be met, and the VC file's path.
