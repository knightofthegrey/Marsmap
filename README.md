# Marsmap

**NASA Space Apps Challenge 2026 · Seattle · November 14–15, 2026**
**Challenge:** [Interplanetary Survival Guide: Martian Map](https://www.spaceappschallenge.org/2026/challenges/interplanetary-survival-guide-martian-map/) (Intermediate)

> Status: kickoff.

---

## The challenge

The first astronauts on Mars will need a better map than any we have today. NASA has decades of orbital and rover data about the planet, but it hasn't been layered together for the scale astronauts will actually work at: a single spacewalk across a few kilometres of terrain. Tools like JPL's Mars Trek are built for regional and global views, not for planning one specific Marswalk.

The challenge asks teams to create a map, tool or interface that a Mars astronaut could use during a surface mission, built from NASA data. In NASA's words, it should help "plan and carry out a successful Marswalk while conducting new and exciting science along the way."

What NASA suggests considering:

- **Start from a science goal** and choose a location that serves it. NASA's own example is the boxwork formations on Mount Sharp, which the Curiosity rover studied in 2025.
- **Layer at least three kinds of data**, for example optical, thermal, spectral, topographic, geophysical, atmospheric, radiation or chemical.
- **Put astronaut safety first.** Account for the Martian radiation environment, and for suits and carried equipment limiting mobility. Include a route that avoids major obstacles such as large rock formations.
- **Pick a format:** a planning display inside the habitat, a tablet the crew carries, a heads-up display (HUD) on the helmet visor, or something new.
- **Look beyond Mars:** see how NASA has mapped the Moon and other worlds for inspiration.
- **Show the astronaut what to expect** by including point-of-view imagery from Mars surface missions.

## Our idea

**A helmet heads-up display that guides an astronaut through a planned Marswalk at Mount Sharp, Gale Crater.**

- **Science goal:** visit the boxwork ridges on Mount Sharp. These are thought to have formed when groundwater flowed through cracks in the rock, which raises questions about how long Mars could have supported microbial life.
- **Layered map:** orbital imagery, terrain elevation and slope, thermal data (loose sand vs. rock), radiation, and weather, combined into one view of a roughly 2 km area.
- **Safe route:** a path planned around steep slopes and obstacles, linking a series of science stops.
- **Science stops:** each stop has a short task and a Curiosity rover photo of the spot, so the astronaut knows what it should look like before they arrive.
- **HUD design informed by the Moon:** Apollo and Artemis show that astronauts on foot struggle most with judging distance and knowing where they are relative to home. Our HUD will lead with a home arrow, a next-target arrow, hazard warnings and time remaining, rather than a cluttered map.
- **Simulated live conditions:** radiation and weather readings replayed from real Curiosity measurements, plus scripted events such as a dust gust, streamed to the HUD during the walk.

### Planned components

| Part | Plan |
|---|---|
| Data preparation | Python scripts to crop and convert NASA terrain and imagery, compute slope and a hazard map, and prepare science stops |
| Live telemetry | A WebSocket service that streams replayed radiation and weather data and mission events |
| Astronaut HUD | A Unity first-person demo on real Mars terrain, with a top-down hazard minimap |

## Resources

### Listed on the official challenge page

- [JPL Mars Trek](https://trek.nasa.gov/mars/): NASA's web portal for exploring Mars data, including landing sites
- [USGS–NASA Planetary Geologic Mapping Program](https://astrogeology-usgs.hub.arcgis.com/pages/interactive-maps-mars): standardized geologic maps of Mars
- [Curiosity's proposed path up Mount Sharp](https://science.nasa.gov/resource/curiositys-proposed-path-up-mount-sharp/): an example route map for the region
- [NASA Worldview](https://worldview.earthdata.nasa.gov/): Earth satellite imagery explorer (for inspiration)
- [Mars Perseverance raw images](https://mars.nasa.gov/mars2020/multimedia/raw-images/): over a million images from the rover

### NASA data we plan to use

- [HiRISE terrain models and imagery](https://stac.astrogeology.usgs.gov/browser-dev/#/api/collections/mro_hirise_socet_dtms) (Mars Reconnaissance Orbiter, via USGS Astrogeology)
- [THEMIS thermal inertia mosaics](https://astrogeology.usgs.gov/search/map/themis_thermal_inertia_mosaic_quantitative_30s060e_100mpp) (Mars Odyssey, via USGS Astrogeology)
- [Curiosity RAD radiation data](https://pds-ppi.igpp.ucla.edu/data/MSL-M-RAD-3-RDR-V1.0/) (NASA Planetary Data System)
- [Curiosity REMS weather data](https://pds-atmospheres.nmsu.edu/) (NASA Planetary Data System, Atmospheres Node)
- [Curiosity raw images](https://mars.nasa.gov/raw_images/128093?site=msl) (NASA/JPL-Caltech)

### Tools and references

- [JMARS](https://jmars.asu.edu/): Arizona State University's Mars mapping tool, for scouting the site
- [NASA Space Apps Challenge](https://www.spaceappschallenge.org/): event information and rules

## Team

Nate
Ahrial
Aiden
Aidan

## License

This project will be released under an open-source license, as Space Apps requires. All data sources will be credited in this README.
