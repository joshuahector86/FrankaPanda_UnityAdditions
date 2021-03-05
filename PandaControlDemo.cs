﻿using System;
using System.Collections;
using System.Threading;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using UnityEngine;
using System.IO;

// Any cs class in the assets folder will be available for Unity scripts, without needing explicit importing

// Control scripts must inherit from MonoBehaviours. Attach your control script (like this one) to the root body of the robot articulation.


public class PandaControlDemo : MonoBehaviour
{
    //Write to file 
    private string Filename = "Assets/placeholder.csv";
    private FileStream dataLog;
    StreamWriter dataWriter;

    // Declare an instance of a panda robot.
    public PandaRobot PD;

    // Declare any shared variables. Public variables will be visible in the GUI (under the monobehaviour script dropdown)
    public Vector3 positionSensor; // Cartesian position is always encoded as a Vector3 (x,y,z). Note that in Unity, Y is the vertical axis

    public bool ReadyToStart = false; // flag to indicate the robot is ready to start executing the control script. See below for details.

    // Select which kind of controller to use by setting the appropriate flag:
    public bool ForwardKinematicControl = true;
    public bool InverseKinematicControl = false;
    public bool TorqueControl = false;

    // Some private control variables:
    private Vector<float> FKJointPositions;
    private Matrix4x4 IKGoalPose;
    private Queue<Vector<float>> LPFiltVals = new Queue<Vector<float>>(); // low pass filter for acceleration
    private Vector<float> TC_stiffness;
    private Vector<float> TC_damping;

    public bool FKControlInitialised = false;
    public bool IKControlInitialised = false;
    public bool TorqueControlInitialised = false;
    public bool jointTorqueErrorCheck = false;

    // Use this for initialization
    void Start()
    {

        //Opening the file for data writing
        dataLog = File.Create(Filename);
        dataWriter = new StreamWriter(dataLog);


        // the start subroutine runs exactly once, when you start the simulation
        // Use this to initialise variables and class instances

        PD = new PandaRobot(gameObject); // Initialise our robot (this will use default values for inertias, joint limits, etc)

        // Initialise kinematics and joints
        PD.Articulation.JointUpdate();

        // the PandaRobot class by default uses a basic inverse kinematic solver, described in the FastIterSolve class.
        // We can adjust the number of iterations, maximum convergence error, etc. 
        PD.IKSolver.converge_eps = 1e-4f;

        // Upon import, the initial position of the robot is at a joint singularity, and has some overlap of the collision meshes.
        // We generally move the robot to a neutral position before beginning a control script, to ensure smooth behaviour.

        // PD.q_initial stores the default robot joint state values (as seen in the real hardware)
        // PD.q_goal_state stores a vector of goal joint positions for use by the joint drivers. 

        PD.q_goal_state = Vector<float>.Build.DenseOfVector(PD.q_initial);

        /* If you change the values in PD.q_initial or PD.q_goal_state, it is a good idea to make sure they don't violate
         * the maximum/minimum joint angles. The subroutine JointLimitConstraints in the FastIterSolve class will ensure the goal joint positions
         * do not violate the joint constraints, eg:
         */
        PD.IKSolver.JointLimitConstraints(PD.Articulation, PD.q_initial).CopyTo(PD.q_goal_state);

        // Initialise the variables for our three kinds of controller

        // Forward Kinematic Control: set a vector of goal joint positions
        FKJointPositions = Vector<float>.Build.DenseOfVector(PD.q_initial);
        FKJointPositions[0] += -80.0f;
        FKJointPositions[1] -= 22.5f;
        FKJointPositions[2] += 30.0f;
        FKJointPositions[3] += 22.5f;
        FKJointPositions[4] += 45.0f;
        FKJointPositions[5] += 45.0f;

        // Inverse Kinematic Control: set a desired end effector pose matrix (encoded as a 4x4 matrix transform in the robot base frame)

        // Use Matrix4x4.TRS to build a transform matrix from a position vector and rotation quaternion:
        Vector3 goalPosition = new Vector3(PD.Articulation.base2EETransform.m03, PD.Articulation.base2EETransform.m13, PD.Articulation.base2EETransform.m23);
        goalPosition += new Vector3(-0.15f, -0.3f, -0.15f);
        IKGoalPose = Matrix4x4.TRS(goalPosition, PD.Articulation.base2EETransform.rotation, new Vector3(1, 1, 1));

    }


    //Closing the file that was opened for data writing
    private void OnApplicationQuit()
    {
        Debug.Log("Application ending after " + Time.time + " seconds");
        dataWriter.Close();
        dataLog.Close();

    }


    // Update is called once per frame
    void FixedUpdate()
    {
        // Use FixedUpdate (fixed time step simulation) for anything involving real physics

        // JointUpdate should be run at least once every control loop, to ensure the internal model of the kinematics
        // matches the actual joint position of the robot. 

        PD.Articulation.JointUpdate();

        if (jointTorqueErrorCheck)
        {
            TorqueControl = true;
            ForwardKinematicControl = false; 
        }

        // Read sensor data: At every time step, you can read the position, velocity, and acceleration of any
        // joint or linkage in the articulation, using the linkage index (i=0...6):

        // PD.Articulation.segments[i].linkedBody.jointPosition (position of joint)
        // PD.Articulation.segments[i].linkedBody.jointVelocity ( velocity of joint)
        // PD.Articulation.segments[i].linkedBody.jointAcceleration (acceleration of joint)
        // PD.Articulation.segments[i].linkedBody.transform.position (position in world frame, as a Vector3)
        // PD.Articulation.segments[i].linkedBody.transform.rotation (rotation in world frame, as a quaternion)

        // To access every joint position, velocity, acceleration at once, read a Vector<float> from:

        // PD.Articulation.jointState (position)
        // PD.qd_state (velocity)
        // PD.qdd_state (acceleration)

        // You can also quickly access the end effector pose in the robot base frame using PD.Articulation.base2EETransform
        // Note that this is a 4x4 matrix, so position and rotation will need to be extracted manually, eg:
        positionSensor = new Vector3(PD.Articulation.base2EETransform.m03, PD.Articulation.base2EETransform.m13, PD.Articulation.base2EETransform.m23);

        // Make sure the robot is in a neutral position before we begin to control it
        if (!ReadyToStart)
        {
            /* Drive the joints towards the goal position.
             *
             * The movement is governed by the internal driver parameters (k_stiffness, k_damping)
             * and the maximum velocity allowed for the joints
             *
             * Set maximum velocity using PD.velocity (see PandaRobot.cs)
             *
             * Joint drive parameters can be set in several ways:
             * 
             * To set the stiffness or damping for the whole chain, create a vector of stiffness or damping values equal in length
             * to the number of joints (eg k_stiffness, k_damping), and call
             * PD.UpdateJointStiffness(k_stiffness);
             * PD.UpdateJointDamping(k_damping);
             *
             * To set individual joint drive parameters, either user
             * PD.SetStiffness(linkage_to_set, stiffness_value);
             * PD.SetDamping(linkage_to_set, damping_value)
             * 
             * or adjust them manually using the GUI
             */

            PD.DriveJointsIncremental(PD.Articulation.jointState, PD.q_goal_state);

            // When we have reached the neutral starting position, set the flag and the robot will now transition to using
            // the active controller.

            if ((PD.q_goal_state - PD.Articulation.jointState).L2Norm() < 1.0f)
            {
                ReadyToStart = true;
            }
        }

        else if (ForwardKinematicControl) 
        {
            /* Drive each joint of the robot to a specified angle
             * add the same stiffness and damping lines her
             * can lower velocity as well 
             * PD.velocity
             * Move the robot to a safe position here.
             */
            if (!FKControlInitialised)
            {
                FKJointPositions.CopyTo(PD.q_goal_state);
                FKControlInitialised = true;
            }

            PD.DriveJointsIncremental(PD.Articulation.jointState, PD.q_goal_state);

        }

        else if (InverseKinematicControl)
        {
            /* Set a goal end effector position and rotation, use the built-in iterative inverse kinematic solver to find an appropriate
             * set of joint positions, given the current joint state.
             * If the solver cannot find an appropriate set of joint angles within the given maximum iterations, it will send up an warning in
             * the GUI console and use the last entry in the iteration as the goal joint positions
             */

            if (!IKControlInitialised)
            {
                Vector<float> q_update = PD.SolveInverseKinematics(IKGoalPose, PD.Articulation.jointState);
                q_update.CopyTo(PD.q_goal_state);
                IKControlInitialised = true;
            }

            PD.DriveJointsIncremental(PD.Articulation.jointState, PD.q_goal_state);

        }

        else if (TorqueControl)
        {
            /* Instead of driving the positions of the joints, we can directly input a desired torque.
             * 
             * This example controller is a force-follow controller, which will let a human guide the robot by exerting a force on it.
             * It does this by setting the joint impedance very low, and inputting only the torque needed to
             * counteract gravity. This torque is calculated using the robot's internal model of its dynamic and inertial parameters.
             */

            if (!TorqueControlInitialised)
            {
                /* initialise the torque control structures

                * Note that the torque control depends heavily on the joint acceleration. Because the joints are
                * impedance controlled (and hence quite 'springy'), this can be a noisy value. I have implemented a low-pass filter
                * on the acceleration to ensure smoother behaviour.
                *
                * First, call the joint dynamic updater. This will initialise the dynamic parameters - mass and dynamic inertia matrices,
                * coriolis forces, etc.
                */
                PD.UpdateJointDynamics();

                // Initialise the low-pass acceleration filter:
                Vector<float> alpha0 = Vector<float>.Build.DenseOfVector(PD.qdd_state);
                for (int i_queue = 0; i_queue < 3; i_queue++)
                {
                    LPFiltVals.Enqueue(alpha0);
                }

                // Set stiffness, damping in joints
                TC_stiffness = Vector<float>.Build.Dense(PD.Articulation.numberJoints, 800.0f);
                TC_damping = Vector<float>.Build.Dense(PD.Articulation.numberJoints, 40.0f);

                PD.UpdateJointStiffness(TC_stiffness);
                PD.UpdateJointDamping(TC_damping);

                TorqueControlInitialised = true;

            }

            // filter acceleration input:
            Vector<float> filtered_qdd = Vector<float>.Build.Dense(PD.Articulation.numberJoints);
            filtered_qdd += PD.qdd_state; //acceleration of each joint
            int filtCount = 1;

            if (LPFiltVals.Count > 0)
            {
                filtered_qdd += LPFiltVals.Dequeue();
                filtCount++;

                if (LPFiltVals.Count > 1)
                {
                    filtered_qdd += LPFiltVals.Peek();
                    filtCount++;
                }

            }
            filtered_qdd /= filtCount;
            Vector<float> qdd_state_update = Vector<float>.Build.DenseOfVector(PD.qdd_state);
            LPFiltVals.Enqueue(qdd_state_update);
            // Threshold joint acceleration for last joint to avoid feedback instability from gripper control
            if (filtered_qdd[6] > 0.05f) { filtered_qdd[6] = 0.05f; }
            else if (filtered_qdd[6] < -0.05f) { filtered_qdd[6] = -0.05f; }


            // Calculate and update the robot dynamics
            PD.CalculateInertiaMatrix();
            PD.UpdateJointDynamics();
            PD.UpdateCoriolis();

            // Calculate necessary torque using Torque = M * qdd_state + Cv * qd_state + G
            Vector<float> jointTorque = PD.DynamicInertia * (filtered_qdd) + PD.CoriolisMatrix * PD.qd_state + PD.GravityCompForces; //PD.qd_state = list of velocity vectors


            // set joint states so joint drive doesn’t fight torque
           //PD.DriveJointsIncremental(PD.Articulation.jointState, jointGoal);

            // apply torque
            PD.ApplyJointForces(jointTorque);

            //Joint torque = (all the torques required to hold up the robot) + (torque required to move to joint position)
            //Torque required to move to joint position = stiffness*joint_error
            //stifness can dtermine how fast the error goes to zero


            // (If you want the robot to return to its initial position after being released, you can
            // use PD.ApplyJointForces in conjunction with PD.DriveJointsIncremental, but note that this means the robot will
            // fight a little with any external forces applied)


            // To read joint forces: (update from relase 2020.2.b14)
            // need to calculate appropriate length of the list of floats, which is equal to the total # of degrees of freedom
            // of the body including gripper fingers, etc

            int dof_robot = PD.Articulation.segments[2].linkedBody.dofCount;

            List<float> jointForces_return = new List<float>(dof_robot);

            PD.Articulation.segments[2].linkedBody.GetJointForces(jointForces_return);
            // foreach (float force in jointForces_return)              
        }
        
        //New variable torque_expected to hold all of the torques we are getting here
        //calculate torque using the different components
        int i = 0;
       
        Vector<float> torque_expected = Vector<float>.Build.Dense(PD.Articulation.GetNbrJoints());
        Vector<float> torque_real = Vector<float>.Build.Dense(PD.Articulation.GetNbrJoints());
        
        //Go through each segment to get the individual joints
        foreach (Segment seg in PD.Articulation.segments)
        {           
            if (seg.joint.jointType == ArticulationJointType.RevoluteJoint)
            {

                ArticulationDrive controlDrive = seg.linkedBody.xDrive;
                float targetPosition = controlDrive.target;
                float jointStiffness = controlDrive.stiffness;
                float jointDamping = controlDrive.damping;

                /* Calculate torque for both real and expected. Change expected by some factoring in order to show a difference between the two for plot puposes
                 * To add noise for the purpose of testing, alter torque_real
                 * Maybe in matlab Add sino amplitude(degrees here- example 45)*sine(omega - oscilation freq* time)
                 * incorporate a difference in jointstiffness and damping - do it in a percentage - just make sure this is going to change - proves attack on hardware
                */
                
                torque_expected[i] = jointStiffness * ((seg.linkedBody.jointPosition[0] - targetPosition) * 3.14f / 360f) + jointDamping * (seg.linkedBody.jointVelocity[0] * 3.14f / 360f);
                torque_real[i] = jointStiffness * ((seg.linkedBody.jointPosition[0] - .90f*targetPosition) * 3.14f / 360f) + jointDamping * (seg.linkedBody.jointVelocity[0] * 3.14f / 360f);
                i++;

                //Comparison of real and expected torques
                foreach (float jointError in ((torque_real - torque_expected) / torque_expected))
                {
                    //Error addjustment here
                    if (Math.Abs(jointError) > .05)
                    {
                        //When the robot is falling outside of the expected range of motion, switch to torque mode so that we can make the motion of
                        //the robot to the expected torque 
                        //jointTorqueErrorCheck = true;

                        Debug.Log("A security check is needed. Unexpected Motion has occured.");                                
                    }
                    else
                    {
                        Debug.Log("There is no abnormal movement in the robot");
                        Debug.Log(torque_real);
                    }
                }
            }
        }

        //Data writing code for joint 2 only
        float data_three = torque_expected[1];
        float data_four = torque_real[1];
        float T_error = .05f * torque_expected[1];
        dataWriter.Write(Time.realtimeSinceStartup);
        dataWriter.Write(" s, ");
        //Joint 2 
        dataWriter.Write(data_three);
        dataWriter.Write(" , ");
        dataWriter.WriteLine(data_four);
        dataWriter.Write(" , ");
        dataWriter.Write(T_error);
        dataWriter.WriteLine(" , ");
    }
}
