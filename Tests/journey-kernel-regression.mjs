import fs from "node:fs";
import path from "node:path";

const root=path.resolve(import.meta.dirname,"..");
const read=relative=>fs.readFileSync(path.join(root,relative),"utf8");
const assert=(condition,message)=>{if(!condition)throw new Error(message)};
const migration=read("Source/Sql/CustomerJourneyKernelV440.sql");
const program=read("Source/Program.cs");
const repository=read("Source/Services/CustomerJourneyRepository.cs");

for(const table of ["JourneyLeads","JourneyOpportunities","JourneyOpportunityStageHistory","JourneyWorkItems","JourneyEvents","JourneyOutbox"])
  assert(migration.includes(`OBJECT_ID(N'dbo.${table}',N'U') IS NULL`),`${table} is not guarded for repeatable migration.`);
for(const destructive of [/\bDROP\s+TABLE\b/i,/\bTRUNCATE\s+TABLE\b/i,/\bDELETE\s+FROM\s+dbo\.Customer/i])
  assert(!destructive.test(migration),`Destructive migration statement found: ${destructive}`);
assert(migration.includes("OwnerSellerKey nvarchar(80) NOT NULL"),"Owner invariant is missing.");
assert(migration.includes("NextActionAtUtc datetime2(0) NOT NULL"),"Next-action invariant is missing.");
assert(migration.includes("SlaDueAtUtc datetime2(0) NOT NULL"),"SLA invariant is missing.");
assert(program.includes('GetSection("JourneyKernel")'),"Journey feature options are not registered.");
assert(program.includes('appsettings.JourneyKernel.local.json'),"Local Journey feature flag file is not loaded.");
assert(repository.includes("CaptureInteractionBestEffortAsync"),"Backward-compatible interaction capture is missing.");
assert(repository.includes("without affecting Seller v2"),"Seller v2 failure isolation is missing.");
console.log("v4.4.0 Journey Kernel regression passed.");
