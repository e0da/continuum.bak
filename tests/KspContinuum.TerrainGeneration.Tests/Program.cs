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
int[] owners = { -12, 34 }; string[] geometry = { initial, initial }; float[] matrices = { 1, 0, 0, 1 };
if (!TerrainGenerationAgreement.Matches(owners, (int[])owners.Clone(), geometry, (string[])geometry.Clone(), matrices, (float[])matrices.Clone())) throw new Exception("Identical generation rejected.");
if (TerrainGenerationAgreement.Matches(owners, new[] { -13, 34 }, geometry, geometry, matrices, matrices)) throw new Exception("Foreign native owner accepted.");
if (TerrainGenerationAgreement.Matches(owners, owners, geometry, new[] { initial, new string('0',64) }, matrices, matrices)) throw new Exception("In-place geometry change accepted.");
if (TerrainGenerationAgreement.Matches(owners, owners, geometry, geometry, matrices, new float[] { 1, .0001f, 0, 1 })) throw new Exception("Changed collider pose accepted.");
Console.WriteLine("PASS fixed-boundary generation identity, content and pose agreement.");
