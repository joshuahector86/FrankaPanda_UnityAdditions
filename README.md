# FrankaPanda_Unity
Unity implementation of the Franka Panda platform, using impedance joint drivers; 
Initial Unity Package provided by Nicole E Carey. To find changes related to the implementation of Security Trigger (threat detection and resillience measure) please see the 

Requirements: Works with Unity 2020.1 or above. Note that force sensing on joints is not (yet) enabled, though this functionality exists in PhysX and may be exposed in Unity in the future.
Linkage lengths, masses, inertias match the real Panda body as closely as possible. Due to convex mesh limitations, the robot collision envelope is slightly larger than the visualisation mesh.
