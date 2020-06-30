// copyright insert
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Rock.Model;
using Rock.Data;
using Rock;

namespace com.passioncitychurch.Model
{
    public class AttendanceService
    {
        /// <summary>
        /// Automatically schedules people for attendance for the specified attendanceOccurrences. See summary of <see cref="SchedulePersonsAutomatically(int, DateTime, PersonAlias)" /> for how this works,
        /// </summary>
        /// <param name="attendanceOccurrenceIdList">The attendance occurrence identifier list.</param>
        /// <param name="scheduledByPersonAlias">The scheduled by person alias.</param>
        public void SchedulePersonsAutomaticallyForAttendanceOccurrences(List<int> attendanceOccurrenceIdList,int resourceGroupId, bool isDataView, PersonAlias scheduledByPersonAlias, Rock.Model.AttendanceService service)
        {
            var rockContext = service.Context as RockContext;
            var groupLocationQry = new GroupLocationService(rockContext).Queryable();
            var attendanceOccurrencesQry = new AttendanceOccurrenceService(rockContext).GetByIds(attendanceOccurrenceIdList)
                .Where(a => a.GroupId.HasValue && a.LocationId.HasValue && a.ScheduleId.HasValue);

            // sort the occurrences by the Date, then by the associated GroupLocation.Order then by Location.name
            var sortedAttendanceOccurrenceList = attendanceOccurrencesQry
                .Join(groupLocationQry,
                        ao => new { GroupId = ao.GroupId.Value, LocationId = ao.LocationId.Value },
                        gl => new { gl.GroupId, gl.LocationId }, (ao, gl) => new
                        {
                            AttendanceOccurrence = ao,
                            GroupLocation = gl
                        })
                        .OrderBy(a => a.AttendanceOccurrence.OccurrenceDate)
                        .ThenBy(a => a.GroupLocation.Order)
                        .ThenBy(a => a.GroupLocation.Location.Name)
                        .Select(a => a.AttendanceOccurrence)
                        .AsNoTracking()
                        .ToList();

            foreach (var attendanceOccurrence in sortedAttendanceOccurrenceList)
            {
                SchedulePersonsAutomaticallyForAttendanceOccurrence(attendanceOccurrence, resourceGroupId, isDataView, scheduledByPersonAlias, service);
            }
        }


        /// <summary>
        /// Automatically schedules people for attendance for the specified attendanceOccurrence. See summary of <see cref="SchedulePersonsAutomatically(int, DateTime, PersonAlias)"/> for how this works,
        /// </summary>
        /// <param name="attendanceOccurrence">The attendance occurrence.</param>
        /// <param name="scheduledByPersonAlias">The scheduled by person alias.</param>
        private void SchedulePersonsAutomaticallyForAttendanceOccurrence(AttendanceOccurrence attendanceOccurrence, int resourceGroupId, bool isDataView, PersonAlias scheduledByPersonAlias, Rock.Model.AttendanceService service)
        {
            int locationId = attendanceOccurrence.LocationId.Value;
            int scheduleId = attendanceOccurrence.ScheduleId.Value;
            int attendanceOccurrenceId = attendanceOccurrence.Id;

            // get the capacity settings for the Group/Location/Schedule. NOTE: The Database and UI don't prevent duplicate LocationIds for a group,
            // but since that really doesn't make sense, just grab the first one
            int? desiredCapacity = new GroupLocationService(service.Context as RockContext).Queryable()
                .Where(gl => gl.GroupId == attendanceOccurrence.GroupId)
                .Where(gl => gl.LocationId == locationId)
                .SelectMany(gl => gl.GroupLocationScheduleConfigs)
                .Where(sc => sc.ScheduleId == scheduleId)
                .Select(a => a.DesiredCapacity)
                .FirstOrDefault();


            // First, get any attendance records for members that signed up for an unspecified location for selected Group and Schedule
            var unspecifiedLocationResourceAttendanceList = service.Queryable().Where(a =>
                a.Occurrence.ScheduleId == attendanceOccurrence.ScheduleId
                && a.Occurrence.GroupId == attendanceOccurrence.GroupId
                && a.Occurrence.OccurrenceDate == attendanceOccurrence.OccurrenceDate
               && a.Occurrence.LocationId == null).ToList();

            // get all available resources (group members that have a schedule template matching the occurrence date and schedule)
            var schedulerResourceParameters = new SchedulerResourceParameters
            {
                AttendanceOccurrenceGroupId = attendanceOccurrence.GroupId.Value,
                AttendanceOccurrenceScheduleId = attendanceOccurrence.ScheduleId.Value,
                AttendanceOccurrenceSundayDate = attendanceOccurrence.OccurrenceDate.SundayDate(),
                ResourceGroupId = resourceGroupId
                //GroupMemberFilterType = SchedulerResourceGroupMemberFilterType.ShowMatchingPreference
            };

            var schedulerResources = service.GetSchedulerResources(schedulerResourceParameters);

            // only grab resources that haven't been scheduled already, and don't have a conflict (blackout or scheduled for some other group)
            // Also, only include resources that have a GroupMemberId since those would be the only ones that would be auto-schedulable
            var scheduleResourcesGroupMemberIds = schedulerResources
                .Where(a => a.GroupMemberId.HasValue && a.IsAlreadyScheduledForGroup == false && a.HasConflict == false)
                .Select(a => a.GroupMemberId).ToList();

            //var scheduleResourcesPersonIds = schedulerResources
            //    .Where(a => a.GroupMemberId.HasValue && a.IsAlreadyScheduledForGroup == false && a.HasConflict == false)
            //    .Select(a => a.PersonId).ToList();

            // use a new RockContext to use for adding attending resources so that get can get saved to the database without saving any changes associated with the current rockContext
            var groupAssignmentAttendanceRockContext = new RockContext();
            var groupAssignmentAttendanceService = new Rock.Model.AttendanceService(groupAssignmentAttendanceRockContext);

            if (isDataView)
            {
                //if (!scheduleResourcesPersonIds.Any())
                //{
                //    // nobody to add as scheduled resource, so return
                //    return;
                //}

                // if the Persons are coming from a data view, let's change the approach
                DataViewService resourceDataViews = new DataViewService(service.Context as RockContext);
                DataView resourceView = resourceDataViews.Get(resourceGroupId);

                // iterate over people in Data View
                List<string> errorMessages = new List<string>();

                var entityQry = resourceView.GetQuery(null, service.Context as RockContext, 300, out errorMessages);

                var personQry = new PersonService(service.Context as RockContext)

                    .Queryable().Where(p => entityQry.Select(t => t.Id).Contains(p.Id))

                    .ToList();



                foreach (var person in personQry)
                {
                    //if (scheduleResourcesPersonIds.Contains(person.Id))
                    //{
                        groupAssignmentAttendanceService.ScheduledPersonAddPending(person.Id, attendanceOccurrenceId, scheduledByPersonAlias);
                        groupAssignmentAttendanceRockContext.SaveChanges();
                    //}
                }

            }
            else
            {
                if (!schedulerResources.Any())
                {
                    // nobody to add as scheduled resource, so return
                    return;
                }

                // create new group member service so we can call all members for auto-scheduling
                GroupMemberService gmService = new GroupMemberService(service.Context as RockContext);

                var groupMembers = gmService.GetByGroupId(resourceGroupId);


                foreach (var groupMember in groupMembers)
                {
                    //if (scheduleResourcesGroupMemberIds.Contains(groupMember.Id))
                    //{
                        groupAssignmentAttendanceService.ScheduledPersonAddPending(groupMember.PersonId, attendanceOccurrenceId, scheduledByPersonAlias);
                        groupAssignmentAttendanceRockContext.SaveChanges();
                    //}
                    
                }
            }
            
            

        }

        private class GroupMemberAssignmentInfo
        {
            public int GroupMemberId { get; set; }
            public int PersonId { get; set; }
            public bool SpecificLocationAndSchedule { get; set; }
            public bool SpecificScheduleOnly { get; set; }
            public bool SpecificLocationOnly { get; set; }
            public int? LocationId { get; internal set; }
            public int? ScheduleId { get; internal set; }
        }
    }
}
