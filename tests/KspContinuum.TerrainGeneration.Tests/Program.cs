using System;
using System.IO;
using KspContinuum;

float[] positions = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
int[] triangles = { 0, 1, 2 };
string initial = TerrainGeometryFingerprint.Compute(positions, triangles);
if (initial != TerrainGeometryFingerprint.Compute((float[])positions.Clone(), (int[])triangles.Clone())) throw new Exception("Equivalent geometry differs.");
triangles[1] = 2; triangles[2] = 1;
if (initial == TerrainGeometryFingerprint.Compute(positions, triangles)) throw new Exception("In-place winding edit lost.");
triangles[1] = 1; triangles[2] = 2; positions[3] = 1.0001f;
if (initial == TerrainGeometryFingerprint.Compute(positions, triangles)) throw new Exception("In-place vertex edit lost.");
triangles[2] = 3;
try { TerrainGeometryFingerprint.Compute(positions, triangles); throw new Exception("Invalid index accepted."); } catch (InvalidDataException) { }
Console.WriteLine("PASS geometry equivalence, in-place winding/vertex changes, malformed index.");
